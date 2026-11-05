namespace B_VIP.Contracts.Admin
{
    public sealed record AdminOverviewDto(
        int TotalUsers,
        int ActiveUsers,
        int SuspendedUsers,
        int TotalLicenses,
        int ActiveLicenses,
        int ExpiredLicenses,
        int TotalActivations,
        int OpenTickets,
        int OperationsLast24h,
        int FailedOperationsLast24h,
        int SecurityEventsLast24h,
        DateTime? LastBackupUtc,
        string ServerStatus,
        string DatabaseStatus,
        string IntegrationsStatus,
        DateTime GeneratedUtc);

    /// <summary>Live counters for the technician management console.</summary>
    public sealed record TechnicianMetricsDto(
        int TotalTechnicians,
        int ActiveSubscriptions,
        int PendingVerifications,
        int Suspended,
        int HardwareBindings,
        int OperationsLogged,
        DateTime GeneratedUtc);

    /// <summary>Result of an administrative HWID decoupling.</summary>
    public sealed record ResetHardwareIdResponse(
        Guid UserId,
        string UserName,
        string? HardwareId,
        int HardwareIdResetCount,
        string Message);

    public sealed record VerifyEmailResponseDto(Guid UserId, string UserName, bool EmailVerified, string Message);

    public sealed record SetEmailVerifiedRequest(bool Verified);

    public sealed record ExtendSubscriptionRequest(int Days, string? Reason = null);

    public sealed record ExtendSubscriptionResponse(
        Guid LicenseId,
        string LicenseKey,
        string PlanName,
        DateTime? ExpiresAt,
        string Message);

    public sealed record SettingDto(
        string Key,
        string? Value,
        bool IsSecret,
        string? Description,
        string? Category,
        DateTime UpdatedUtc);

    public sealed record UpdateSettingRequest(string? Value);

    public sealed record SettingsResponse(IReadOnlyList<SettingDto> Settings);

    public sealed record IntegrationDto(
        Guid Id,
        string Code,
        string DisplayName,
        string Kind,
        string? ToolPath,
        string? BaseUrl,
        bool IsEnabled,
        bool IsConfigured,
        bool HasSecret,
        DateTime UpdatedUtc,
        IntegrationHealthDto? LastHealthCheck);

    public sealed record IntegrationHealthDto(
        DateTime CheckedUtc,
        bool Succeeded,
        int? LatencyMs,
        int? HttpStatusCode,
        string? Message,
        string Status);

    public sealed record CreateIntegrationRequest(
        string Code,
        string DisplayName,
        string Kind,
        string? ToolPath = null,
        string? BaseUrl = null,
        string? ApiKey = null,
        bool IsEnabled = false,
        string? ConfigurationJson = null);

    public sealed record UpdateIntegrationRequest(
        string? DisplayName,
        string? ToolPath,
        string? BaseUrl,
        string? ApiKey,
        bool? IsEnabled,
        string? ConfigurationJson);

    public sealed record TestIntegrationResponse(
        bool Succeeded,
        string Message,
        int? LatencyMs,
        int? HttpStatusCode,
        DateTime CheckedUtc);

    public sealed record AuditEventDto(
        Guid Id,
        DateTime OccurredUtc,
        string? ActorName,
        string Action,
        string? TargetType,
        string? TargetId,
        string Outcome,
        string? Detail,
        string? IpAddress,
        string? CorrelationId);

    public sealed record AuditEventListResponse(
        IReadOnlyList<AuditEventDto> Items,
        int TotalCount,
        int Page,
        int PageSize);

    public sealed record HealthResponse(
        string Overall,
        IReadOnlyList<Contracts.Common.HealthComponentDto> Components,
        DateTime CheckedUtc);

    public sealed record ReleaseDto(
        Guid Id,
        string Version,
        string ReleaseNotes,
        string? MinSupportedVersion,
        bool IsMandatory,
        string? DownloadUrl,
        string? Sha256,
        long? SizeBytes,
        DateTime PublishedUtc,
        bool Enabled);

    public sealed record CreateReleaseRequest(
        string Version,
        string ReleaseNotes,
        string? MinSupportedVersion = null,
        bool IsMandatory = false,
        string? DownloadUrl = null,
        string? Sha256 = null,
        long? SizeBytes = null);

    public sealed record BackupDto(
        Guid Id,
        string Status,
        DateTime StartedUtc,
        DateTime? CompletedUtc,
        string? FilePath,
        long? SizeBytes,
        string? Checksum,
        DateTime? VerifiedUtc,
        DateTime? RestoreTestedUtc,
        string? Error,
        string? PerformedBy);

    public sealed record CreateBackupRequest(string? Note = null);

    public sealed record RestoreBackupRequest(Guid BackupId, bool Confirm);

    public sealed record BackupActionResponse(Guid Id, string Status, string Message);

    public sealed record NotificationDto(
        Guid Id,
        string Type,
        string Title,
        string Body,
        string? Link,
        string? Source,
        DateTime CreatedUtc,
        DateTime? ReadUtc);

    public sealed record NotificationListResponse(
        IReadOnlyList<NotificationDto> Items,
        int UnreadCount,
        int TotalCount,
        int Page,
        int PageSize);

    public sealed record MaintenanceDto(
        bool Enabled,
        string? Message,
        DateTime? StartsAt,
        DateTime? EndsAt,
        DateTime UpdatedUtc);

    public sealed record UpdateMaintenanceRequest(bool Enabled, string? Message, DateTime? StartsAt, DateTime? EndsAt);

    public sealed record SystemStatusDto(
        string ServerVersion,
        string RuntimeVersion,
        string Environment,
        string DatabaseProvider,
        bool DatabaseConnected,
        int? PendingMigrations,
        bool MaintenanceEnabled,
        DateTime UtcNow,
        long ProcessUptimeSeconds,
        double CpuPercent,
        long WorkingSetBytes);
}
