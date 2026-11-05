namespace B_VIP.Core.Entities
{
    /// <summary>One executed (or queued) service operation, recorded end to end.</summary>
    public class DeviceServiceOperation
    {
        public Guid Id { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public string OperationType { get; set; } = string.Empty;
        public string? TargetDeviceId { get; set; }
        public string? TargetDeviceDescription { get; set; }
        public Guid? LicenseId { get; set; }
        public OperationStatus Status { get; set; } = OperationStatus.Queued;
        public DateTime QueuedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public int? ExitCode { get; set; }
        public bool CancellationRequested { get; set; }
        public string? ToolPath { get; set; }
        public string? ToolVersion { get; set; }
        public string SuccessCondition { get; set; } = "exit-code-zero";
        public string? OutputText { get; set; }
        public string? ErrorText { get; set; }
        public string? Summary { get; set; }
        public string? FailureReason { get; set; }
        public Guid? DiagnosticReportId { get; set; }
        public DiagnosticReport? DiagnosticReport { get; set; }
        public int TimeoutSeconds { get; set; } = 120;
        public string? IpAddress { get; set; }
        public string? ClientVersion { get; set; }

        public bool IsTerminal => Status is OperationStatus.Completed
            or OperationStatus.Failed
            or OperationStatus.Cancelled
            or OperationStatus.TimedOut;
    }

    public class OperationEvent
    {
        public Guid Id { get; set; }
        public Guid OperationId { get; set; }
        public DeviceServiceOperation Operation { get; set; } = null!;
        public DateTime OccurredUtc { get; set; }
        public string Level { get; set; } = "Information";
        public string Message { get; set; } = string.Empty;
        public string? Data { get; set; }
    }

    public class DiagnosticReport
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public Guid? OperationId { get; set; }
        public string? DeviceId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Module { get; set; } = string.Empty;
        public string ContentJson { get; set; } = "{}";
        public bool ContainsSensitiveData { get; set; }
        public bool Redacted { get; set; }
        public DateTime CreatedUtc { get; set; }
        public int SizeBytes { get; set; }
        public string? CorrelationId { get; set; }
    }

    public class ReportAttachment
    {
        public Guid Id { get; set; }
        public Guid DiagnosticReportId { get; set; }
        public DiagnosticReport DiagnosticReport { get; set; } = null!;
        public Guid StoredFileId { get; set; }
        public StoredFileMetadata StoredFile { get; set; } = null!;
        public string FileName { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
    }
}
