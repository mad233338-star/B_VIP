using System.Diagnostics;
using System.Security.Cryptography;
using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Common;
using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.Core.Entities;
using B_VIP.Infrastructure.Data;
using B_VIP.Infrastructure.Options;
using B_VIP.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>Administration backend: every figure is read from the database or measured from the host.</summary>
    public sealed class AdminService : IAdminService
    {
        private readonly B_VIPDbContext _db;
        private readonly IAuditLogger _audit;
        private readonly ISecretProtector _protector;
        private readonly AuthOptions _authOptions;
        private readonly StorageOptions _storageOptions;
        private readonly IWebHostEnvironmentAccessor? _environment;
        private readonly IHttpClientFactory? _httpClientFactory;
        private readonly ILogger<AdminService> _logger;
        private string? _backupNote;

        public AdminService(
            B_VIPDbContext db,
            IAuditLogger audit,
            ISecretProtector protector,
            IOptions<AuthOptions> authOptions,
            IOptions<StorageOptions> storageOptions,
            ILogger<AdminService> logger,
            IWebHostEnvironmentAccessor? environment = null,
            IHttpClientFactory? httpClientFactory = null)
        {
            _db = db;
            _audit = audit;
            _protector = protector;
            _authOptions = authOptions.Value;
            _storageOptions = storageOptions.Value;
            _logger = logger;
            _environment = environment;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<AdminOverviewDto> GetOverviewAsync(CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;
            DateTime dayAgo = now.AddDays(-1);

            int totalUsers = await _db.Users.CountAsync(u => !u.IsDeleted, ct);
            int activeUsers = await _db.Users.CountAsync(u => !u.IsDeleted && u.Status == AccountStatus.Active, ct);
            int suspendedUsers = await _db.Users.CountAsync(u => !u.IsDeleted && u.Status == AccountStatus.Suspended, ct);

            int totalLicenses = await _db.Licenses.CountAsync(ct);
            int activeLicenses = await _db.Licenses.CountAsync(l =>
                l.Status == LicenseStatus.Active &&
                (l.ExpiresAt == null || l.ExpiresAt > now), ct);
            int expiredLicenses = await _db.Licenses.CountAsync(l =>
                l.Status == LicenseStatus.Expired ||
                (l.Status == LicenseStatus.Active && l.ExpiresAt != null && l.ExpiresAt <= now), ct);
            int totalActivations = await _db.LicenseActivations.CountAsync(a => a.DeactivatedUtc == null, ct);

            int openTickets = await _db.ServiceTickets.CountAsync(t =>
                t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved, ct);
            int operationsLast24h = await _db.DeviceServiceOperations.CountAsync(o => o.QueuedUtc >= dayAgo, ct);
            int failedOperations = await _db.DeviceServiceOperations.CountAsync(o =>
                o.CompletedUtc >= dayAgo &&
                (o.Status == OperationStatus.Failed || o.Status == OperationStatus.TimedOut), ct);
            int securityEvents = await _db.SecurityEvents.CountAsync(e => e.OccurredUtc >= dayAgo, ct);

            DateTime? lastBackup = await _db.BackupRecords
                .Where(b => b.Status == "Succeeded")
                .OrderByDescending(b => b.CompletedUtc)
                .Select(b => b.CompletedUtc)
                .FirstOrDefaultAsync(ct);

            string databaseStatus = await _db.Database.CanConnectAsync(ct) ? "Healthy" : "Unhealthy";

            DateTime healthWindow = now.AddMinutes(-15);
            int failingIntegrations = await _db.IntegrationHealthChecks
                .CountAsync(h => h.CheckedUtc >= healthWindow && !h.Succeeded, ct);
            int configuredIntegrations = await _db.IntegrationConfigurations.CountAsync(i => i.IsEnabled, ct);
            string integrationsStatus = configuredIntegrations == 0
                ? "NotConfigured"
                : failingIntegrations > 0 ? "Degraded" : "Healthy";

            return new AdminOverviewDto(
                totalUsers, activeUsers, suspendedUsers,
                totalLicenses, activeLicenses, expiredLicenses, totalActivations,
                openTickets, operationsLast24h, failedOperations, securityEvents,
                lastBackup,
                "Healthy",
                databaseStatus,
                integrationsStatus,
                now);
        }

        public async Task<SettingsResponse> GetSettingsAsync(CancellationToken ct = default)
        {
            List<ApplicationSetting> settings = await _db.ApplicationSettings.AsNoTracking()
                .OrderBy(s => s.Category).ThenBy(s => s.Key)
                .ToListAsync(ct);

            return new SettingsResponse(settings.Select(s => new SettingDto(
                s.Key,
                s.IsSecret ? null : s.Value,
                s.IsSecret,
                s.Description,
                s.Category,
                s.UpdatedUtc)).ToList());
        }

        public async Task<SettingDto?> UpdateSettingAsync(string key, string? value, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            ApplicationSetting? setting = await _db.ApplicationSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (setting is null)
            {
                throw new DomainException("setting-not-found", $"Unknown setting '{key}'.", 404);
            }

            string? stored = value;
            if (setting.IsSecret)
            {
                if (string.IsNullOrEmpty(value))
                {
                    stored = null;
                }
                else if (!_protector.IsConfigured)
                {
                    throw new DomainException("secret-store-not-configured",
                        "The server secret encryption key is not configured; secret settings cannot be stored.", 500);
                }
                else
                {
                    stored = _protector.Protect(value);
                }
            }

            setting.Value = stored;
            setting.UpdatedByUserId = actorUserId;
            setting.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "settings.update", "setting", key,
                AuditOutcome.Success, setting.IsSecret ? "Secret value replaced." : $"Value='{value}'",
                null, correlationId, ct);

            return new SettingDto(setting.Key, setting.IsSecret ? null : setting.Value,
                setting.IsSecret, setting.Description, setting.Category, setting.UpdatedUtc);
        }

        public async Task<IReadOnlyList<IntegrationDto>> GetIntegrationsAsync(CancellationToken ct = default)
        {
            List<IntegrationConfiguration> integrations = await _db.IntegrationConfigurations.AsNoTracking()
                .OrderBy(i => i.Code)
                .ToListAsync(ct);

            var latest = await _db.IntegrationHealthChecks.AsNoTracking()
                .GroupBy(h => h.IntegrationId)
                .Select(g => g.OrderByDescending(h => h.CheckedUtc).First())
                .ToListAsync(ct);

            var byId = latest.ToDictionary(h => h.IntegrationId, h => h);

            return integrations.Select(i => MapIntegration(i, byId.GetValueOrDefault(i.Id))).ToList();
        }

        public async Task<IntegrationDto> CreateIntegrationAsync(CreateIntegrationRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.Code) || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                throw new DomainException("invalid-request", "Code and display name are required.");
            }

            if (!Enum.TryParse<IntegrationKind>(request.Kind, true, out IntegrationKind kind))
            {
                throw new DomainException("invalid-kind", $"Unknown integration kind '{request.Kind}'.");
            }

            string code = request.Code.Trim().ToLowerInvariant();
            if (await _db.IntegrationConfigurations.AnyAsync(i => i.Code == code, ct))
            {
                throw new DomainException("integration-exists", "An integration with that code already exists.", 409);
            }

            DateTime now = DateTime.UtcNow;
            var integration = new IntegrationConfiguration
            {
                Id = Guid.NewGuid(),
                Code = code,
                DisplayName = request.DisplayName.Trim(),
                Kind = kind,
                ToolPath = request.ToolPath,
                BaseUrl = request.BaseUrl,
                IsEnabled = request.IsEnabled,
                ConfigurationJson = request.ConfigurationJson,
                CreatedUtc = now,
                UpdatedUtc = now,
                UpdatedByUserId = actorUserId
            };

            ApplyApiKey(integration, request.ApiKey);
            _db.IntegrationConfigurations.Add(integration);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "integration.create", "integration", integration.Id.ToString(),
                AuditOutcome.Success, $"{integration.Code} ({integration.Kind})", null, correlationId, ct);

            return MapIntegration(integration, null);
        }

        public async Task<IntegrationDto?> UpdateIntegrationAsync(Guid id, UpdateIntegrationRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            IntegrationConfiguration? integration = await _db.IntegrationConfigurations.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (integration is null) return null;

            if (!string.IsNullOrWhiteSpace(request?.DisplayName)) integration.DisplayName = request.DisplayName.Trim();
            if (request?.ToolPath is not null) integration.ToolPath = request.ToolPath;
            if (request?.BaseUrl is not null) integration.BaseUrl = request.BaseUrl;
            if (request?.IsEnabled.HasValue == true) integration.IsEnabled = request.IsEnabled.Value;
            if (request?.ConfigurationJson is not null) integration.ConfigurationJson = request.ConfigurationJson;
            if (request?.ApiKey is not null) ApplyApiKey(integration, request.ApiKey);

            integration.UpdatedUtc = DateTime.UtcNow;
            integration.UpdatedByUserId = actorUserId;
            integration.IsConfigured = IsConfigured(integration);

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "integration.update", "integration", integration.Id.ToString(),
                AuditOutcome.Success, $"enabled={integration.IsEnabled}, configured={integration.IsConfigured}",
                null, correlationId, ct);

            return MapIntegration(integration, null);
        }

        public async Task<TestIntegrationResponse> TestIntegrationAsync(Guid id, CancellationToken ct = default)
        {
            IntegrationConfiguration? integration = await _db.IntegrationConfigurations.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (integration is null)
            {
                return new TestIntegrationResponse(false, "Integration not found.", null, null, DateTime.UtcNow);
            }

            var stopwatch = Stopwatch.StartNew();
            bool success = false;
            string message;
            int? statusCode = null;

            try
            {
                if (!integration.IsEnabled)
                {
                    message = "The integration is disabled. Enable it before testing.";
                }
                else if (!IsConfigured(integration))
                {
                    message = "The integration is not configured.";
                }
                else if (integration.Kind == IntegrationKind.ExternalTool)
                {
                    string path = integration.ToolPath ?? string.Empty;
                    bool exists = File.Exists(path);
                    success = exists;
                    message = exists
                        ? $"Tool found at {Path.GetFileName(path)}."
                        : $"Tool not found at the configured path: {path}";
                }
                else if (!string.IsNullOrWhiteSpace(integration.BaseUrl))
                {
                    using HttpClient client = _httpClientFactory?.CreateClient("integration-test")
                        ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                    using HttpRequestMessage request = new(HttpMethod.Get, integration.BaseUrl);
                    if (!string.IsNullOrEmpty(integration.ApiKeySecret) && _protector.IsConfigured)
                    {
                        try
                        {
                            request.Headers.TryAddWithoutValidation("X-Api-Key", _protector.Unprotect(integration.ApiKeySecret));
                        }
                        catch (Exception)
                        {
                            message = "The stored secret could not be decrypted.";
                            throw new InvalidOperationException(message);
                        }
                    }

                    using HttpResponseMessage response = await client.SendAsync(request, ct);
                    statusCode = (int)response.StatusCode;
                    success = response.IsSuccessStatusCode;
                    message = success
                        ? $"Reached {integration.BaseUrl} with HTTP {statusCode}."
                        : $"Endpoint returned HTTP {statusCode}.";
                }
                else
                {
                    message = "No base URL or tool path is configured for this integration.";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                success = false;
                message = ex is TaskCanceledException
                    ? "The health check timed out."
                    : ex.Message;
            }

            stopwatch.Stop();

            var check = new IntegrationHealthCheck
            {
                Id = Guid.NewGuid(),
                IntegrationId = integration.Id,
                CheckedUtc = DateTime.UtcNow,
                Succeeded = success,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                HttpStatusCode = statusCode,
                Message = message,
                Status = success ? HealthStatus.Healthy : HealthStatus.Unhealthy
            };

            _db.IntegrationHealthChecks.Add(check);
            await _db.SaveChangesAsync(ct);

            return new TestIntegrationResponse(success, message, check.LatencyMs, check.HttpStatusCode, check.CheckedUtc);
        }

        public async Task<AuditEventListResponse> GetAuditEventsAsync(string? action, string? outcome, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default)
        {
            page = Paging.NormalizePage(page);
            pageSize = Paging.NormalizePageSize(pageSize);

            IQueryable<AuditEvent> query = _db.AuditEvents.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(action))
            {
                query = query.Where(e => e.Action.Contains(action));
            }

            if (!string.IsNullOrWhiteSpace(outcome) && Enum.TryParse<AuditOutcome>(outcome, true, out AuditOutcome parsed))
            {
                query = query.Where(e => e.Outcome == parsed);
            }

            if (fromUtc.HasValue) query = query.Where(e => e.OccurredUtc >= fromUtc.Value);
            if (toUtc.HasValue) query = query.Where(e => e.OccurredUtc <= toUtc.Value);

            int total = await query.CountAsync(ct);
            List<AuditEvent> items = await query
                .OrderByDescending(e => e.OccurredUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return new AuditEventListResponse(items.Select(e => new AuditEventDto(
                e.Id, e.OccurredUtc, e.ActorName, e.Action, e.TargetType, e.TargetId,
                e.Outcome.ToString(), e.Detail, e.IpAddress, e.CorrelationId)).ToList(),
                total, page, pageSize);
        }

        public async Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
        {
            var components = new List<Contracts.Common.HealthComponentDto>();
            DateTime now = DateTime.UtcNow;

            // Database
            bool dbConnected = false;
            int pendingMigrations = -1;
            try
            {
                dbConnected = await _db.Database.CanConnectAsync(ct);
                if (dbConnected)
                {
                    pendingMigrations = (await _db.Database.GetPendingMigrationsAsync(ct)).Count();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database health check failed");
            }

            components.Add(new Contracts.Common.HealthComponentDto(
                "database",
                dbConnected ? (pendingMigrations == 0 ? "Healthy" : "Degraded") : "Unhealthy",
                dbConnected
                    ? pendingMigrations == 0
                        ? "Connected; schema up to date."
                        : $"{pendingMigrations} pending migration(s)."
                    : "Cannot connect to the database.",
                now));

            components.Add(new Contracts.Common.HealthComponentDto(
                "backend", "Healthy", "The API process is running.", now));

            // Integrations: one verdict per enabled integration, based on its most recent check.
            DateTime window = now.AddMinutes(-15);
            List<IntegrationConfiguration> integrationRows = await _db.IntegrationConfigurations.AsNoTracking().ToListAsync(ct);
            List<IntegrationHealthCheck> recentChecks = await _db.IntegrationHealthChecks.AsNoTracking()
                .Where(h => h.CheckedUtc >= window)
                .ToListAsync(ct);

            int enabled = integrationRows.Count(i => i.IsEnabled);
            int failing = 0;
            foreach (IntegrationConfiguration integration in integrationRows.Where(i => i.IsEnabled))
            {
                IntegrationHealthCheck? latest = recentChecks
                    .Where(h => h.IntegrationId == integration.Id)
                    .OrderByDescending(h => h.CheckedUtc)
                    .FirstOrDefault();

                if (latest is null || !latest.Succeeded)
                {
                    failing++;
                }
            }

            components.Add(new Contracts.Common.HealthComponentDto(
                "integrations",
                enabled == 0 ? "NotConfigured" : failing > 0 ? "Degraded" : "Healthy",
                enabled == 0 ? "No integration is enabled." : $"{enabled} enabled, {failing} failing.",
                now));

            // Backups
            DateTime? lastBackup = await _db.BackupRecords
                .Where(b => b.Status == "Succeeded")
                .OrderByDescending(b => b.CompletedUtc)
                .Select(b => b.CompletedUtc)
                .FirstOrDefaultAsync(ct);

            bool backupHealthy = lastBackup.HasValue && lastBackup.Value > now.AddHours(-48);
            components.Add(new Contracts.Common.HealthComponentDto(
                "backup",
                lastBackup is null ? "NotConfigured" : backupHealthy ? "Healthy" : "Degraded",
                lastBackup is null
                    ? "No successful backup has been recorded."
                    : $"Last successful backup {lastBackup:u}.",
                now));

            // Migration state surfaced as its own component
            components.Add(new Contracts.Common.HealthComponentDto(
                "migrations",
                pendingMigrations < 0 ? "Unknown" : pendingMigrations == 0 ? "Healthy" : "Degraded",
                pendingMigrations < 0 ? "Could not read the migration state." : $"{pendingMigrations} pending.",
                now));

            string overall = components.Any(c => c.Status == "Unhealthy") ? "Unhealthy"
                : components.Any(c => c.Status is "Degraded" or "Unknown") ? "Degraded"
                : "Healthy";

            return new HealthResponse(overall, components, now);
        }

        public async Task<SystemStatusDto> GetStatusAsync(CancellationToken ct = default)
        {
            bool connected = await _db.Database.CanConnectAsync(ct);
            int pending = connected ? (await _db.Database.GetPendingMigrationsAsync(ct)).Count() : -1;
            bool maintenance = await _db.MaintenanceSettings.AsNoTracking()
                .AnyAsync(m => m.MaintenanceEnabled, ct);

            using Process current = Process.GetCurrentProcess();
            current.Refresh();

            string version = typeof(AdminService).Assembly.GetName().Version?.ToString() ?? "unknown";

            return new SystemStatusDto(
                version,
                Environment.Version.ToString(),
                _environment?.EnvironmentName ?? "Production",
                connected ? "PostgreSQL" : "unknown",
                connected,
                pending < 0 ? null : pending,
                maintenance,
                DateTime.UtcNow,
                (long)(DateTime.UtcNow - current.StartTime.ToUniversalTime()).TotalSeconds,
                Math.Round(Environment.ProcessorCount > 0 ? current.TotalProcessorTime.TotalMilliseconds /
                    Math.Max(1, (DateTime.UtcNow - current.StartTime).TotalMilliseconds) * 100 / Environment.ProcessorCount : 0, 2),
                current.WorkingSet64);
        }

        public async Task<IReadOnlyList<ReleaseDto>> GetReleasesAsync(CancellationToken ct = default)
        {
            return await _db.SoftwareReleases.AsNoTracking()
                .OrderByDescending(r => r.PublishedUtc)
                .Select(r => new ReleaseDto(
                    r.Id, r.Version, r.ReleaseNotes, r.MinSupportedVersion, r.IsMandatory,
                    r.DownloadUrl, r.Sha256, r.SizeBytes, r.PublishedUtc, r.Enabled))
                .ToListAsync(ct);
        }

        public async Task<ReleaseDto> CreateReleaseAsync(CreateReleaseRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.Version))
            {
                throw new DomainException("invalid-request", "A version string is required.");
            }

            string version = request.Version.Trim();
            if (await _db.SoftwareReleases.AnyAsync(r => r.Version == version, ct))
            {
                throw new DomainException("release-exists", "That version already exists.", 409);
            }

            var release = new SoftwareRelease
            {
                Id = Guid.NewGuid(),
                Version = version,
                ReleaseNotes = request.ReleaseNotes ?? string.Empty,
                MinSupportedVersion = request.MinSupportedVersion,
                IsMandatory = request.IsMandatory,
                DownloadUrl = request.DownloadUrl,
                Sha256 = request.Sha256,
                SizeBytes = request.SizeBytes,
                PublishedUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow,
                Enabled = true
            };

            _db.SoftwareReleases.Add(release);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "release.create", "release", release.Id.ToString(),
                AuditOutcome.Success, $"Version {version}", null, correlationId, ct);

            return new ReleaseDto(release.Id, release.Version, release.ReleaseNotes, release.MinSupportedVersion,
                release.IsMandatory, release.DownloadUrl, release.Sha256, release.SizeBytes, release.PublishedUtc, release.Enabled);
        }

        public async Task<ReleaseDto?> UpdateReleaseAsync(Guid id, bool enabled, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            SoftwareRelease? release = await _db.SoftwareReleases.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (release is null) return null;

            release.Enabled = enabled;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "release.update", "release", release.Id.ToString(),
                AuditOutcome.Success, $"enabled={enabled}", null, correlationId, ct);

            return new ReleaseDto(release.Id, release.Version, release.ReleaseNotes, release.MinSupportedVersion,
                release.IsMandatory, release.DownloadUrl, release.Sha256, release.SizeBytes, release.PublishedUtc, release.Enabled);
        }

        public async Task<IReadOnlyList<BackupDto>> GetBackupsAsync(CancellationToken ct = default)
        {
            var rows = await _db.BackupRecords.AsNoTracking()
                .OrderByDescending(b => b.StartedUtc)
                .Take(100)
                .ToListAsync(ct);

            return rows.Select(b => new BackupDto(
                b.Id, b.Status, b.StartedUtc, b.CompletedUtc, b.FilePath, b.SizeBytes,
                b.Checksum, b.VerifiedUtc, b.RestoreTestedUtc, b.Error,
                b.PerformedByUserId.HasValue ? b.PerformedByUserId.ToString() : null)).ToList();
        }

        public async Task<BackupActionResponse> CreateBackupAsync(Guid actorUserId, string? note, string? correlationId, CancellationToken ct = default)
        {
            var record = new BackupRecord
            {
                Id = Guid.NewGuid(),
                PerformedByUserId = actorUserId,
                StartedUtc = DateTime.UtcNow,
                Status = "Running",
                CorrelationId = correlationId
            };

            _db.BackupRecords.Add(record);
            await _db.SaveChangesAsync(ct);

            try
            {
                string directory = Path.GetFullPath(_storageOptions.BackupDirectory);
                Directory.CreateDirectory(directory);

                string fileName = $"b_vip_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{record.Id.ToString("N")[..6]}.dump";
                string filePath = Path.Combine(directory, fileName);

                string? pgDump = ResolveTool(_storageOptions.PgDumpPath, "pg_dump");
                if (pgDump is null)
                {
                    throw new InvalidOperationException(
                        "pg_dump was not found. Set Storage:PgDumpPath to the PostgreSQL pg_dump executable.");
                }

                (string host, int port, string user, string databaseName) = ParseConnectionString();

                (int exitCode, string error) = await RunToolAsync(
                    pgDump,
                    $"--host={host} --port={port} --username={user} --format=custom --file=\"{filePath}\" \"{databaseName}\"",
                    600, ct);

                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"pg_dump exited with code {exitCode}: {Truncate(error, 500)}");
                }

                byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
                string checksum = Convert.ToHexString(SHA256.HashData(bytes));

                record.Status = "Succeeded";
                record.CompletedUtc = DateTime.UtcNow;
                record.FilePath = filePath;
                record.SizeBytes = bytes.LongLength;
                record.Checksum = checksum;
                record.VerifiedUtc = DateTime.UtcNow;
                _backupNote = Truncate(note, 500);
            }
            catch (Exception ex)
            {
                record.Status = "Failed";
                record.CompletedUtc = DateTime.UtcNow;
                record.Error = Truncate(ex.Message, 2000);
                _logger.LogError(ex, "Backup failed");
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "backup.create", "backup", record.Id.ToString(),
                record.Status == "Succeeded" ? AuditOutcome.Success : AuditOutcome.Failure,
                $"{record.Status} {record.SizeBytes} bytes. {_backupNote}", null, correlationId, ct);

            return new BackupActionResponse(record.Id, record.Status,
                record.Status == "Succeeded"
                    ? $"Backup written to {record.FilePath} and checksummed."
                    : $"Backup failed: {record.Error}");
        }

        public async Task<BackupActionResponse?> RestoreBackupAsync(Guid backupId, bool confirm, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            BackupRecord? record = await _db.BackupRecords.FirstOrDefaultAsync(b => b.Id == backupId, ct);
            if (record is null) return null;

            if (!confirm)
            {
                throw new DomainException("confirmation-required", "Restoring a backup requires explicit confirmation.", 422);
            }

            if (record.Status != "Succeeded" || record.FilePath is null || !File.Exists(record.FilePath))
            {
                return new BackupActionResponse(backupId, record.Status, "The backup file is not available for restore.");
            }

            string? pgRestore = ResolveTool(_storageOptions.PgRestorePath, "pg_restore");
            if (pgRestore is null)
            {
                return new BackupActionResponse(backupId, "Failed",
                    "pg_restore was not found. Set Storage:PgRestorePath to the PostgreSQL pg_restore executable.");
            }

            (string host, int port, string user, string databaseName) = ParseConnectionString();

            (int exitCode, string error) = await RunToolAsync(
                pgRestore,
                $"--host={host} --port={port} --username={user} --clean --if-exists --no-owner --dbname=\"{databaseName}\" \"{record.FilePath}\"",
                900, ct);

            if (exitCode != 0)
            {
                await _audit.LogAsync(actorUserId, null, "backup.restore", "backup", backupId.ToString(),
                    AuditOutcome.Failure, $"exit={exitCode}", null, correlationId, ct);
                return new BackupActionResponse(backupId, "Failed", $"pg_restore exited with {exitCode}: {Truncate(error, 400)}");
            }

            record.RestoreTestedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "backup.restore", "backup", backupId.ToString(),
                AuditOutcome.Success, "Restore completed.", null, correlationId, ct);

            return new BackupActionResponse(backupId, "Succeeded", "Restore completed successfully.");
        }

        public async Task<MaintenanceDto> GetMaintenanceAsync(CancellationToken ct = default)
        {
            MaintenanceSetting setting = await _db.MaintenanceSettings.AsNoTracking().FirstOrDefaultAsync(ct)
                ?? new MaintenanceSetting { Id = Guid.NewGuid(), UpdatedUtc = DateTime.UtcNow };

            return new MaintenanceDto(setting.MaintenanceEnabled, setting.Message, setting.StartsAt, setting.EndsAt, setting.UpdatedUtc);
        }

        public async Task<MaintenanceDto> UpdateMaintenanceAsync(UpdateMaintenanceRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            MaintenanceSetting? setting = await _db.MaintenanceSettings.FirstOrDefaultAsync(ct);
            if (setting is null)
            {
                setting = new MaintenanceSetting { Id = Guid.NewGuid() };
                _db.MaintenanceSettings.Add(setting);
            }

            setting.MaintenanceEnabled = request.Enabled;
            setting.Message = request.Message;
            setting.StartsAt = request.StartsAt;
            setting.EndsAt = request.EndsAt;
            setting.UpdatedByUserId = actorUserId;
            setting.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "settings.maintenance", "maintenance", setting.Id.ToString(),
                AuditOutcome.Success, $"enabled={request.Enabled}", null, correlationId, ct);

            return new MaintenanceDto(setting.MaintenanceEnabled, setting.Message, setting.StartsAt, setting.EndsAt, setting.UpdatedUtc);
        }

        public async Task<IReadOnlyList<SettingDto>> GetPublicSettingsAsync(CancellationToken ct = default)
        {
            // Only non-secret, explicitly public keys are handed to clients before sign-in.
            return await _db.ApplicationSettings.AsNoTracking()
                .Where(s => !s.IsSecret && (s.Category == "public" || s.Category == "branding"))
                .OrderBy(s => s.Key)
                .Select(s => new SettingDto(s.Key, s.Value, s.IsSecret, s.Description, s.Category, s.UpdatedUtc))
                .ToListAsync(ct);
        }

        private void ApplyApiKey(IntegrationConfiguration integration, string? apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                integration.ApiKeySecret = null;
                return;
            }

            if (!_protector.IsConfigured)
            {
                throw new DomainException("secret-store-not-configured",
                    "The server secret encryption key is not configured; API keys cannot be stored.", 500);
            }

            integration.ApiKeySecret = _protector.Protect(apiKey);
        }

        private static bool IsConfigured(IntegrationConfiguration integration) => integration.Kind switch
        {
            IntegrationKind.ExternalTool => !string.IsNullOrWhiteSpace(integration.ToolPath),
            _ => !string.IsNullOrWhiteSpace(integration.BaseUrl)
        };

        private static IntegrationDto MapIntegration(IntegrationConfiguration i, IntegrationHealthCheck? health) => new(
            i.Id,
            i.Code,
            i.DisplayName,
            i.Kind.ToString(),
            i.ToolPath,
            i.BaseUrl,
            i.IsEnabled,
            i.IsConfigured,
            !string.IsNullOrEmpty(i.ApiKeySecret),
            i.UpdatedUtc,
            health is null ? null : new IntegrationHealthDto(
                health.CheckedUtc, health.Succeeded, health.LatencyMs, health.HttpStatusCode,
                health.Message, health.Status.ToString()));

        private (string Host, int Port, string User, string Database) ParseConnectionString()
        {
            string connectionString = _db.Database.GetConnectionString() ?? string.Empty;
            string host = "127.0.0.1", user = "postgres", database = "b_vip_dev";
            int port = 5432;

            foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=', 2);
                if (pair.Length != 2) continue;

                string name = pair[0].Trim();
                string value = pair[1].Trim();

                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Server", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (name.Equals("Port", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out port);
                else if (name.Equals("Username", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("User Id", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("UserID", StringComparison.OrdinalIgnoreCase)) user = value;
                else if (name.Equals("Database", StringComparison.OrdinalIgnoreCase)) database = value;
            }

            return (host, port, user, database);
        }

        private static string? ResolveTool(string? configuredPath, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                return configuredPath;
            }

            string? pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }

            return null;
        }

        private static async Task<(int ExitCode, string Error)> RunToolAsync(string executable, string arguments, int timeoutSeconds, CancellationToken ct)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return (-1, $"Failed to start {executable}");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                string error = await process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, error);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process already exited.
                }

                return (-2, "The tool exceeded its timeout and was terminated.");
            }
        }

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }

    /// <summary>Minimal abstraction so the infrastructure does not depend on the web host directly.</summary>
    public interface IWebHostEnvironmentAccessor
    {
        string EnvironmentName { get; }
    }
}
