using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Common;

namespace B_VIP.Application.Interfaces
{
    /// <summary>Administration: overview, settings, integrations, audit, health, releases and backups.</summary>
    public interface IAdminService
    {
        Task<AdminOverviewDto> GetOverviewAsync(CancellationToken ct = default);

        Task<SettingsResponse> GetSettingsAsync(CancellationToken ct = default);

        Task<SettingDto?> UpdateSettingAsync(string key, string? value, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<IntegrationDto>> GetIntegrationsAsync(CancellationToken ct = default);

        Task<IntegrationDto> CreateIntegrationAsync(CreateIntegrationRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IntegrationDto?> UpdateIntegrationAsync(Guid id, UpdateIntegrationRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<TestIntegrationResponse> TestIntegrationAsync(Guid id, CancellationToken ct = default);

        Task<AuditEventListResponse> GetAuditEventsAsync(string? action, string? outcome, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default);

        Task<HealthResponse> GetHealthAsync(CancellationToken ct = default);

        Task<SystemStatusDto> GetStatusAsync(CancellationToken ct = default);

        Task<IReadOnlyList<ReleaseDto>> GetReleasesAsync(CancellationToken ct = default);

        Task<ReleaseDto> CreateReleaseAsync(CreateReleaseRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<ReleaseDto?> UpdateReleaseAsync(Guid id, bool enabled, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<BackupDto>> GetBackupsAsync(CancellationToken ct = default);

        Task<BackupActionResponse> CreateBackupAsync(Guid actorUserId, string? note, string? correlationId, CancellationToken ct = default);

        Task<BackupActionResponse?> RestoreBackupAsync(Guid backupId, bool confirm, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<MaintenanceDto> GetMaintenanceAsync(CancellationToken ct = default);

        Task<MaintenanceDto> UpdateMaintenanceAsync(UpdateMaintenanceRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<SettingDto>> GetPublicSettingsAsync(CancellationToken ct = default);
    }

    /// <summary>Creates the first Super Admin and the reference data on an empty database.</summary>
    public interface IDataSeeder
    {
        Task<SeedResult> EnsureSeededAsync(CancellationToken ct = default);

        Task<(bool Success, string Message)> CreateFirstSuperAdminAsync(string userName, string email, string password, string? ipAddress, CancellationToken ct = default);
    }

    public sealed record SeedResult(int RolesCreated, int PermissionsCreated, int PlansCreated, int CategoriesCreated, int SettingsCreated, int IntegrationsCreated, bool WasAlreadySeeded);
}
