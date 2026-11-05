namespace B_VIP.Contracts.Operations
{
    public sealed record CreateOperationRequest(
        string OperationType,
        string? DeviceId = null,
        string? DeviceDescription = null,
        IReadOnlyDictionary<string, string>? Parameters = null,
        bool ConfirmDataLoss = false,
        int? TimeoutSeconds = null);

    public sealed record OperationDto(
        Guid Id,
        string CorrelationId,
        string OperationType,
        string Status,
        string? TargetDeviceId,
        string? TargetDeviceDescription,
        DateTime QueuedUtc,
        DateTime? StartedUtc,
        DateTime? CompletedUtc,
        int? ExitCode,
        string? ToolPath,
        string? ToolVersion,
        string SuccessCondition,
        string? Summary,
        string? FailureReason,
        string? Output,
        string? ErrorOutput,
        Guid? DiagnosticReportId,
        string UserName,
        Guid UserId,
        bool CancellationRequested);

    public sealed record OperationEventDto(
        Guid Id,
        DateTime OccurredUtc,
        string Level,
        string Message);

    public sealed record OperationListResponse(
        IReadOnlyList<OperationDto> Items,
        int TotalCount,
        int Page,
        int PageSize);

    public sealed record OperationCreatedResponse(Guid Id, string CorrelationId, string Status);

    /// <summary>Records the real outcome of a tool run that was executed on the client machine.</summary>
    public sealed record RecordOperationResultRequest(
        int? ExitCode,
        bool TimedOut,
        bool Cancelled,
        string? Output,
        string? ErrorOutput,
        string? Summary,
        bool Verified);

    public sealed record CancelOperationResponse(bool Cancelled, string Status, string Message);
}

namespace B_VIP.Contracts.Reports
{
    public sealed record CreateReportRequest(
        string Title,
        string Module,
        string? DeviceId,
        string ContentJson,
        bool ContainsSensitiveData,
        bool Redact = false);

    public sealed record DiagnosticReportDto(
        Guid Id,
        string Title,
        string Module,
        string? DeviceId,
        string ContentJson,
        bool ContainsSensitiveData,
        bool Redacted,
        DateTime CreatedUtc,
        int SizeBytes,
        string UserName,
        Guid UserId,
        Guid? OperationId);

    public sealed record ReportListResponse(
        IReadOnlyList<DiagnosticReportDto> Items,
        int TotalCount,
        int Page,
        int PageSize);
}
