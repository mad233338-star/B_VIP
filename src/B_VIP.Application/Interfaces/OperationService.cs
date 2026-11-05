using B_VIP.Contracts.Common;
using B_VIP.Core;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Reports;

namespace B_VIP.Application.Interfaces
{
    public interface IOperationService
    {
        Task<OperationCreatedResponse> CreateAsync(CreateOperationRequest request, Guid userId, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<OperationListResponse> SearchAsync(string? status, string? type, Guid? userId, int page, int pageSize, CancellationToken ct = default);

        Task<OperationDto?> GetAsync(Guid id, CancellationToken ct = default);

        Task<IReadOnlyList<OperationEventDto>> GetEventsAsync(Guid id, CancellationToken ct = default);

        /// <summary>
        /// Requests cancellation. A user may always cancel their own operation; cancelling somebody
        /// else's requires the operations.cancel permission, which the caller signals via
        /// <paramref name="cancelAnyUser"/>.
        /// </summary>
        Task<CancelOperationResponse> RequestCancelAsync(Guid id, Guid userId, string? correlationId, bool cancelAnyUser = false, CancellationToken ct = default);

        /// <summary>Records the real outcome of a locally executed tool run.</summary>
        Task<OperationDto?> RecordResultAsync(Guid id, int? exitCode, bool timedOut, bool cancelled, string? output, string? errorOutput, string? summary, bool verified, CancellationToken ct = default);
    }

    public interface IReportService
    {
        Task<DiagnosticReportDto> CreateAsync(CreateReportRequest request, Guid userId, string? correlationId, CancellationToken ct = default);

        Task<ReportListResponse> SearchAsync(string? module, Guid? userId, int page, int pageSize, CancellationToken ct = default);

        Task<DiagnosticReportDto?> GetAsync(Guid id, CancellationToken ct = default);
    }

    public interface INotificationService
    {
        Task NotifyAsync(Guid userId, string type, string title, string body, string? link = null, string? source = null, CancellationToken ct = default);

        Task NotifyRoleAsync(string roleName, string type, string title, string body, string? link = null, string? source = null, CancellationToken ct = default);

        Task<Contracts.Admin.NotificationListResponse> SearchAsync(Guid userId, bool unreadOnly, int page, int pageSize, CancellationToken ct = default);

        Task<bool> MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default);

        Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default);

        Task<int> PurgeExpiredAsync(DateTime olderThanUtc, CancellationToken ct = default);
    }

    /// <summary>Append-only audit trail. Records can be read by administrators but never modified through the API.</summary>
    public interface IAuditLogger
    {
        Task LogAsync(
            Guid? actorUserId,
            string? actorName,
            string action,
            string? targetType,
            string? targetId,
            AuditOutcome outcome,
            string? detail,
            string? ipAddress,
            string? correlationId,
            CancellationToken ct = default);
    }
}
