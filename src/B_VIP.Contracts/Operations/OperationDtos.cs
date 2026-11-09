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

    /// <summary>
    /// One task in the cloud service queue. The fields are plain strings so a task that the
    /// backend describes loosely still renders, and every value is optional for the same reason.
    /// </summary>
    public sealed record ServiceQueueTaskDto(
        string? Id = null,
        string? Type = null,
        string? Status = null,
        string? User = null,
        DateTime? QueuedUtc = null,
        string? Result = null);

    /// <summary>
    /// The service queue envelope: <c>success</c>, <c>status</c> and <c>tasks</c> are always
    /// present, so an empty queue is a connected queue rather than an error.
    /// </summary>
    public sealed record ServiceQueueResponse(
        bool Success = false,
        string? Status = null,
        bool Authenticated = false,
        string? UserName = null,
        DateTime? GeneratedUtc = null,
        IReadOnlyList<ServiceQueueTaskDto>? Tasks = null);
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
