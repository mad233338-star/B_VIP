using B_VIP.Application.Interfaces;
using B_VIP.Application.Services;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Reports;
using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.Core.Entities;
using B_VIP.Core.Operations;
using B_VIP.Core.Rules;
using B_VIP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>
    /// Persistence for the service operation engine. The desktop client executes the external tool,
    /// then reports the real outcome back through <see cref="RecordResultAsync"/>.
    /// </summary>
    public sealed class OperationService : IOperationService
    {
        private readonly B_VIPDbContext _db;
        private readonly IAuditLogger _audit;
        private readonly ILicenseService _licenses;
        private readonly ILogger<OperationService> _logger;

        public OperationService(B_VIPDbContext db, IAuditLogger audit, ILicenseService licenses, ILogger<OperationService> logger)
        {
            _db = db;
            _audit = audit;
            _licenses = licenses;
            _logger = logger;
        }

        public async Task<OperationCreatedResponse> CreateAsync(CreateOperationRequest request, Guid userId, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.OperationType))
            {
                throw new DomainException("invalid-request", "Operation type is required.");
            }

            OperationTypeDefinition? definition = OperationTypes.Find(request.OperationType);
            if (definition is null)
            {
                throw new DomainException("unknown-operation", $"Unknown operation type '{request.OperationType}'.", 400);
            }

            if (definition.DataLossRisk && !request.ConfirmDataLoss)
            {
                throw new DomainException("confirmation-required",
                    "This operation can destroy data and requires an explicit user confirmation.", 422);
            }

            // The operation is only accepted while the account holds a valid license.
            Contracts.Auth.LicenseSummaryDto? license = await _licenses.GetSummaryForUserAsync(userId, ct);
            if (license is null)
            {
                throw new DomainException("license-required", "An active license is required to run service operations.", 403);
            }

            if (!string.Equals(license.Status, nameof(LicenseStatus.Active), StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("license-not-active",
                    $"The license is not active ({license.Status}). Operations are not permitted.", 403);
            }

            DateTime now = DateTime.UtcNow;
            string id = correlationId ?? Guid.NewGuid().ToString("N");

            var operation = new DeviceServiceOperation
            {
                Id = Guid.NewGuid(),
                CorrelationId = id,
                UserId = userId,
                OperationType = definition.Key,
                TargetDeviceId = Truncate(request.DeviceId, 128),
                TargetDeviceDescription = Truncate(request.DeviceDescription, 256),
                LicenseId = license.LicenseId,
                Status = OperationStatus.Validating,
                QueuedUtc = now,
                StartedUtc = null,
                SuccessCondition = definition.SuccessCondition,
                TimeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? definition.DefaultTimeoutSeconds, 5, 3600),
                IpAddress = ipAddress,
                ClientVersion = Truncate(request.Parameters is not null && request.Parameters.TryGetValue("clientVersion", out string? v) ? v : null, 32)
            };

            _db.DeviceServiceOperations.Add(operation);
            _db.OperationEvents.Add(new OperationEvent
            {
                Id = Guid.NewGuid(),
                OperationId = operation.Id,
                OccurredUtc = now,
                Level = "Information",
                Message = $"Operation queued: {definition.DisplayName}"
            });

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "operation.create", "operation", operation.Id.ToString(),
                AuditOutcome.Success, $"{definition.Key} device={request.DeviceId ?? "n/a"}", ipAddress, correlationId, ct);

            return new OperationCreatedResponse(operation.Id, operation.CorrelationId, operation.Status.ToString());
        }

        public async Task<OperationListResponse> SearchAsync(string? status, string? type, Guid? userId, int page, int pageSize, CancellationToken ct = default)
        {
            page = Paging.NormalizePage(page);
            pageSize = Paging.NormalizePageSize(pageSize);

            IQueryable<DeviceServiceOperation> query = _db.DeviceServiceOperations.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OperationStatus>(status, true, out OperationStatus parsed))
            {
                query = query.Where(o => o.Status == parsed);
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                query = query.Where(o => o.OperationType == type);
            }

            if (userId.HasValue)
            {
                query = query.Where(o => o.UserId == userId.Value);
            }

            int total = await query.CountAsync(ct);
            List<DeviceServiceOperation> items = await query
                .OrderByDescending(o => o.QueuedUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(o => o.User)
                .ToListAsync(ct);

            return new OperationListResponse(items.Select(Map).ToList(), total, page, pageSize);
        }

        public async Task<OperationDto?> GetAsync(Guid id, CancellationToken ct = default)
        {
            DeviceServiceOperation? operation = await _db.DeviceServiceOperations.AsNoTracking()
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == id, ct);

            return operation is null ? null : Map(operation);
        }

        public async Task<IReadOnlyList<OperationEventDto>> GetEventsAsync(Guid id, CancellationToken ct = default)
        {
            return await _db.OperationEvents.AsNoTracking()
                .Where(e => e.OperationId == id)
                .OrderBy(e => e.OccurredUtc)
                .Select(e => new OperationEventDto(e.Id, e.OccurredUtc, e.Level, e.Message))
                .ToListAsync(ct);
        }

        public async Task<CancelOperationResponse> RequestCancelAsync(Guid id, Guid userId, string? correlationId, bool cancelAnyUser = false, CancellationToken ct = default)
        {
            DeviceServiceOperation? operation = await _db.DeviceServiceOperations.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (operation is null)
            {
                return new CancelOperationResponse(false, "NotFound", "Operation not found.");
            }

            if (operation.UserId != userId && !cancelAnyUser)
            {
                await _audit.LogAsync(userId, null, "operation.cancel", "operation", id.ToString(),
                    AuditOutcome.Denied, "Cancel requested for another user's operation.", null, correlationId, ct);
                // Report the same shape as a missing operation so the caller cannot probe other users' records.
                return new CancelOperationResponse(false, "NotFound", "Operation not found.");
            }

            if (Rule_IsTerminal(operation.Status))
            {
                return new CancelOperationResponse(false, operation.Status.ToString(), "The operation has already finished.");
            }

            operation.CancellationRequested = true;
            DateTime now = DateTime.UtcNow;

            if (operation.Status is OperationStatus.Queued or OperationStatus.Validating)
            {
                operation.Status = OperationStatus.Cancelled;
                operation.CompletedUtc = now;
            }

            _db.OperationEvents.Add(new OperationEvent
            {
                Id = Guid.NewGuid(),
                OperationId = operation.Id,
                OccurredUtc = now,
                Level = "Warning",
                Message = "Cancellation requested by user."
            });

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "operation.cancel", "operation", id.ToString(),
                AuditOutcome.Success, $"Status={operation.Status}", null, correlationId, ct);

            return new CancelOperationResponse(true, operation.Status.ToString(),
                operation.Status == OperationStatus.Cancelled
                    ? "Operation cancelled."
                    : "Cancellation requested; the running tool will be stopped.");
        }

        public async Task<OperationDto?> RecordResultAsync(Guid id, int? exitCode, bool timedOut, bool cancelled, string? output, string? errorOutput, string? summary, bool verified, CancellationToken ct = default)
        {
            DeviceServiceOperation? operation = await _db.DeviceServiceOperations
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == id, ct);

            if (operation is null) return null;

            if (OperationRules.IsTerminal(operation.Status))
            {
                return Map(operation);
            }

            if (operation.CancellationRequested || cancelled)
            {
                cancelled = true;
            }

            OperationStatus resolved = OperationRules.ResolveOutcome(
                exitCode, operation.SuccessCondition, cancelled, timedOut, verified);

            if (!OperationRules.CanTransition(operation.Status, resolved))
            {
                _logger.LogWarning("Rejected transition {From} -> {Output} for operation {Id}",
                    operation.Status, resolved, operation.Id);
                return Map(operation);
            }

            operation.ExitCode = exitCode;
            operation.OutputText = Truncate(output, 200_000);
            operation.ErrorText = Truncate(errorOutput, 100_000);
            operation.Summary = Truncate(summary, 2000);
            operation.Status = resolved;
            operation.CompletedUtc = DateTime.UtcNow;

            if (resolved != OperationStatus.Completed)
            {
                operation.FailureReason = timedOut
                    ? "The tool exceeded its timeout."
                    : cancelled
                        ? "The operation was cancelled."
                        : $"The tool exited with code {exitCode?.ToString() ?? "n/a"} and the success condition '{operation.SuccessCondition}' was not met.";
            }

            _db.OperationEvents.Add(new OperationEvent
            {
                Id = Guid.NewGuid(),
                OperationId = operation.Id,
                OccurredUtc = DateTime.UtcNow,
                Level = resolved == OperationStatus.Completed ? "Information" : "Error",
                Message = $"{resolved} (exit code {exitCode?.ToString() ?? "n/a"})"
            });

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(operation.UserId, null, "operation.result", "operation", operation.Id.ToString(),
                resolved == OperationStatus.Completed ? AuditOutcome.Success : AuditOutcome.Failure,
                $"{operation.OperationType} -> {resolved}, exit={exitCode}", null, null, ct);

            return Map(operation);
        }

        private static bool Rule_IsTerminal(OperationStatus status) => OperationRules.IsTerminal(status);

        private static OperationDto Map(DeviceServiceOperation o) => new(
            o.Id,
            o.CorrelationId,
            o.OperationType,
            o.Status.ToString(),
            o.TargetDeviceId,
            o.TargetDeviceDescription,
            o.QueuedUtc,
            o.StartedUtc,
            o.CompletedUtc,
            o.ExitCode,
            o.ToolPath,
            o.ToolVersion,
            o.SuccessCondition,
            o.Summary,
            o.FailureReason,
            o.OutputText,
            o.ErrorText,
            o.DiagnosticReportId,
            o.User?.UserName ?? string.Empty,
            o.UserId,
            o.CancellationRequested);

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }

    /// <summary>Stores diagnostic reports produced by the desktop diagnostics modules.</summary>
    public sealed class ReportService : IReportService
    {
        private readonly B_VIPDbContext _db;
        private readonly IAuditLogger _audit;

        public ReportService(B_VIPDbContext db, IAuditLogger audit)
        {
            _db = db;
            _audit = audit;
        }

        public async Task<DiagnosticReportDto> CreateAsync(CreateReportRequest request, Guid userId, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.Title))
            {
                throw new DomainException("invalid-request", "A report title is required.");
            }

            string content = request.ContentJson ?? "{}";
            if (request.Redact)
            {
                content = ReportBuilder.Redact(content);
            }

            if (content.Length > 1_000_000)
            {
                throw new DomainException("report-too-large", "The report exceeds the 1 MB limit.", 413);
            }

            DateTime now = DateTime.UtcNow;
            var report = new DiagnosticReport
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = request.Title.Trim(),
                Module = Truncate(request.Module, 64) ?? "general",
                DeviceId = Truncate(request.DeviceId, 128),
                ContentJson = content,
                ContainsSensitiveData = request.ContainsSensitiveData,
                Redacted = request.Redact,
                CreatedUtc = now,
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(content),
                CorrelationId = correlationId
            };

            _db.DiagnosticReports.Add(report);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "report.create", "report", report.Id.ToString(),
                AuditOutcome.Success, $"{report.Module} ({report.SizeBytes} bytes, redacted={report.Redacted})",
                null, correlationId, ct);

            return Map(report, null);
        }

        public async Task<ReportListResponse> SearchAsync(string? module, Guid? userId, int page, int pageSize, CancellationToken ct = default)
        {
            page = Paging.NormalizePage(page);
            pageSize = Paging.NormalizePageSize(pageSize);

            IQueryable<DiagnosticReport> query = _db.DiagnosticReports.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(module))
            {
                query = query.Where(r => r.Module == module);
            }

            if (userId.HasValue)
            {
                query = query.Where(r => r.UserId == userId.Value);
            }

            int total = await query.CountAsync(ct);
            List<DiagnosticReport> items = await query
                .OrderByDescending(r => r.CreatedUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(r => r.User)
                .ToListAsync(ct);

            return new ReportListResponse(items.Select(r => Map(r, r.User?.UserName)).ToList(), total, page, pageSize);
        }

        public async Task<DiagnosticReportDto?> GetAsync(Guid id, CancellationToken ct = default)
        {
            DiagnosticReport? report = await _db.DiagnosticReports.AsNoTracking()
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.Id == id, ct);

            return report is null ? null : Map(report, report.User?.UserName);
        }

        private static DiagnosticReportDto Map(DiagnosticReport r, string? userName) => new(
            r.Id, r.Title, r.Module, r.DeviceId, r.ContentJson, r.ContainsSensitiveData,
            r.Redacted, r.CreatedUtc, r.SizeBytes, userName ?? string.Empty, r.UserId, r.OperationId);

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }
}
