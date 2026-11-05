using B_VIP.Core;

namespace B_VIP.Core.Entities
{
    public class User
    {
        public Guid Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string NormalizedUserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string NormalizedEmail { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public AccountStatus Status { get; set; } = AccountStatus.Active;
        public bool EmailConfirmed { get; set; }
        public int AccessFailedCount { get; set; }
        public DateTime? LockoutEndUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? LastLoginUtc { get; set; }
        public string? Notes { get; set; }
        public bool IsDeleted { get; set; }
        public string? CreatedByUserId { get; set; }

        /// <summary>Hardware identifier of the workstation this technician account is bound to.</summary>
        public string? HardwareId { get; set; }

        /// <summary>How many times an administrator decoupled the account from a workstation.</summary>
        public int HardwareIdResetCount { get; set; }

        /// <summary>SHA-256 hash of the pending e-mail verification token (the token itself is never stored).</summary>
        public string? EmailVerificationTokenHash { get; set; }

        public DateTime? EmailVerificationSentUtc { get; set; }

        /// <summary>SHA-256 hash of the 6-digit activation code that accompanies the link.</summary>
        public string? EmailVerificationCodeHash { get; set; }

        /// <summary>When the 6-digit activation code stops being accepted.</summary>
        public DateTime? EmailVerificationCodeExpiresUtc { get; set; }

        public UserProfile? Profile { get; set; }
        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
        public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
        public ICollection<License> Licenses { get; set; } = new List<License>();
        public ICollection<ServiceTicket> Tickets { get; set; } = new List<ServiceTicket>();
    }

    public class UserProfile
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public string? Phone { get; set; }
        public string? Company { get; set; }
        public string? Country { get; set; }
        public string? PreferredLanguage { get; set; }
        public string? TimeZone { get; set; }
        public DateTime? RegisteredUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public class Role
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string NormalizedName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystem { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
        public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
    }

    public class Permission
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string NormalizedName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Group { get; set; } = string.Empty;
    }

    public class UserRole
    {
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public Guid RoleId { get; set; }
        public Role Role { get; set; } = null!;
        public DateTime AssignedUtc { get; set; }
        public Guid? AssignedByUserId { get; set; }
    }

    public class RolePermission
    {
        public Guid RoleId { get; set; }
        public Role Role { get; set; } = null!;
        public Guid PermissionId { get; set; }
        public Permission Permission { get; set; } = null!;
        public DateTime GrantedUtc { get; set; }
    }

    public class UserSession
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public string SessionTokenHash { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public DateTime? RevokedUtc { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }

        public bool IsActive(DateTime utcNow) => RevokedUtc is null && ExpiresUtc > utcNow;
    }

    public class RefreshToken
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public Guid? SessionId { get; set; }
        public UserSession? Session { get; set; }
        public string TokenHash { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime? RevokedUtc { get; set; }
        public Guid? ReplacedByTokenId { get; set; }
        public string? IpAddress { get; set; }

        public bool IsActive(DateTime utcNow) => RevokedUtc is null && ExpiresUtc > utcNow;
    }

    public class PasswordResetRequest
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public string TokenHash { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime? UsedUtc { get; set; }
        public string? RequestedIp { get; set; }
    }

    public class LoginAttempt
    {
        public Guid Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public bool Succeeded { get; set; }
        public Guid? UserId { get; set; }
        public string? Reason { get; set; }
        public string? IpAddress { get; set; }
        public DateTime OccurredUtc { get; set; }
        public string? CorrelationId { get; set; }
    }

    public class SecurityEvent
    {
        public Guid Id { get; set; }
        public Guid? UserId { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string? IpAddress { get; set; }
        public string? CorrelationId { get; set; }
        public DateTime OccurredUtc { get; set; }
        public AuditOutcome Outcome { get; set; } = AuditOutcome.Success;
    }
}
