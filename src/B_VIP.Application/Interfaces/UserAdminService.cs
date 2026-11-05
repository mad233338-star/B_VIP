using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Users;
using B_VIP.Core.Common;

namespace B_VIP.Application.Interfaces
{
    /// <summary>User directory, roles and permission management. All checks happen on the server.</summary>
    public interface IUserAdminService
    {
        Task<PagedResult<UserSummaryDto>> SearchUsersAsync(string? query, string? status, string? role, int page, int pageSize, CancellationToken ct = default);

        Task<UserProfileDto?> GetUserAsync(Guid id, CancellationToken ct = default);

        Task<UserSummaryDto> CreateUserAsync(CreateUserRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<UserProfileDto?> UpdateUserAsync(Guid id, AdminUpdateUserRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<bool> SuspendUserAsync(Guid id, string reason, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<bool> ReactivateUserAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<int> RevokeSessionsAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<string>> AssignRolesAsync(Guid id, IReadOnlyList<string> roles, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<SessionDto>> GetSessionsAsync(Guid id, CancellationToken ct = default);

        Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default);

        Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<RoleDto?> UpdateRoleAsync(Guid id, UpdateRoleRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<bool> SetRolePermissionsAsync(Guid id, IReadOnlyList<string> permissions, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken ct = default);

        /// <summary>Resolves the effective permission list for a user (server side source of truth).</summary>
        Task<IReadOnlyList<string>> GetPermissionsForUserAsync(Guid userId, CancellationToken ct = default);

        /// <summary>Live technician counters shown at the top of the administration console.</summary>
        Task<TechnicianMetricsDto> GetTechnicianMetricsAsync(CancellationToken ct = default);

        /// <summary>
        /// Decouples a technician from the current workstation: the stored HWID becomes NULL and the
        /// audit counter is incremented. The next sign-in binds the new machine automatically.
        /// </summary>
        Task<ResetHardwareIdResponse> ResetHardwareIdAsync(Guid userId, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        /// <summary>Marks the e-mail address verified (or pending again) without sending a message.</summary>
        Task<VerifyEmailResponseDto> SetEmailVerifiedAsync(Guid userId, bool verified, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        /// <summary>Removes an account from the directory (soft delete) and revokes its sessions.</summary>
        Task<bool> DeleteTechnicianAsync(Guid userId, Guid actorUserId, string? correlationId, CancellationToken ct = default);
    }
}
