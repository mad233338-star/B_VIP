namespace B_VIP.Core.Entities
{
    /// <summary>Server side key/value application setting. Secret values are stored encrypted and never returned by the API.</summary>
    public class ApplicationSetting
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
        public bool IsSecret { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public Guid? UpdatedByUserId { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public class MaintenanceSetting
    {
        public Guid Id { get; set; }
        public bool MaintenanceEnabled { get; set; }
        public string? Message { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public Guid? UpdatedByUserId { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public class IntegrationConfiguration
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public IntegrationKind Kind { get; set; }
        public string? ToolPath { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKeySecret { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsConfigured { get; set; }
        public string? ConfigurationJson { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? UpdatedByUserId { get; set; }

        public ICollection<IntegrationHealthCheck> HealthChecks { get; set; } = new List<IntegrationHealthCheck>();
    }

    public class IntegrationHealthCheck
    {
        public Guid Id { get; set; }
        public Guid IntegrationId { get; set; }
        public IntegrationConfiguration Integration { get; set; } = null!;
        public DateTime CheckedUtc { get; set; }
        public bool Succeeded { get; set; }
        public int? LatencyMs { get; set; }
        public int? HttpStatusCode { get; set; }
        public string? Message { get; set; }
        public HealthStatus Status { get; set; } = HealthStatus.Unknown;
    }

    public class Notification
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public NotificationType Type { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? Link { get; set; }
        public string? Source { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? ReadUtc { get; set; }
    }

    public class SoftwareRelease
    {
        public Guid Id { get; set; }
        public string Version { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string? MinSupportedVersion { get; set; }
        public bool IsMandatory { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
        public long? SizeBytes { get; set; }
        public DateTime PublishedUtc { get; set; }
        public bool ChannelStable { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public DateTime CreatedUtc { get; set; }

        public ICollection<DownloadableReleaseMetadata> Artifacts { get; set; } = new List<DownloadableReleaseMetadata>();
    }

    public class AuditEvent
    {
        public Guid Id { get; set; }
        public Guid? ActorUserId { get; set; }
        public string? ActorName { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public AuditOutcome Outcome { get; set; } = AuditOutcome.Success;
        public string? Detail { get; set; }
        public string? IpAddress { get; set; }
        public string? CorrelationId { get; set; }
        public DateTime OccurredUtc { get; set; }
    }

    public class BackupRecord
    {
        public Guid Id { get; set; }
        public Guid? PerformedByUserId { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public string Status { get; set; } = "Running";
        public string? FilePath { get; set; }
        public long? SizeBytes { get; set; }
        public string? Checksum { get; set; }
        public DateTime? VerifiedUtc { get; set; }
        public DateTime? RestoreTestedUtc { get; set; }
        public string? Error { get; set; }
        public string? CorrelationId { get; set; }
    }

    public class SystemHealthEvent
    {
        public Guid Id { get; set; }
        public string Component { get; set; } = string.Empty;
        public HealthStatus Status { get; set; }
        public string? Message { get; set; }
        public DateTime OccurredUtc { get; set; }
        public string? CorrelationId { get; set; }
    }

    public class DocumentMetadata
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Language { get; set; } = "en";
        public string? Version { get; set; }
        public string? Description { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public class StoredFileMetadata
    {
        public Guid Id { get; set; }
        public Guid? OwnerUserId { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string StoredPath { get; set; } = string.Empty;
        public string? ContentType { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? DeletedUtc { get; set; }
    }

    public class DownloadableReleaseMetadata
    {
        public Guid Id { get; set; }
        public Guid? SoftwareReleaseId { get; set; }
        public SoftwareRelease? SoftwareRelease { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string DownloadUrl { get; set; } = string.Empty;
        public string? Sha256 { get; set; }
        public long? SizeBytes { get; set; }
        public string? Platform { get; set; }
        public bool IsLatest { get; set; }
        public DateTime PublishedUtc { get; set; }
    }
}
