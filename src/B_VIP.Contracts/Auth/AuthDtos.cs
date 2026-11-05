namespace B_VIP.Contracts.Auth
{
    public sealed record LoginRequest(
        string UserName,
        string Password,
        string? DeviceFingerprint = null,
        string? ClientVersion = null,
        bool RememberSession = false);

    public sealed record RefreshRequest(string RefreshToken);

    public sealed record ForgotPasswordRequest(string Email);

    public sealed record ResetPasswordRequest(string Token, string NewPassword);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record FirstAdminRequest(string Token, string UserName, string Email, string Password);

    public sealed record BootstrapStatusDto(bool RequiresFirstAdmin, bool ReferenceDataSeeded);

    public sealed record AuthTokenDto(
        string AccessToken,
        string RefreshToken,
        DateTime ExpiresUtc,
        DateTime RefreshExpiresUtc);

    public sealed record LicenseSummaryDto(
        Guid LicenseId,
        string LicenseKey,
        string PlanCode,
        string PlanName,
        string Status,
        DateTime StartsAt,
        DateTime? ExpiresAt,
        int MaxActivations,
        int CurrentActivations,
        IReadOnlyList<string> Features);

    public sealed record UserSummaryDto(
        Guid Id,
        string UserName,
        string DisplayName,
        string Email,
        string Status,
        IReadOnlyList<string> Roles,
        DateTime CreatedUtc,
        DateTime? LastLoginUtc,
        bool EmailVerified = true,
        string? HardwareId = null,
        int HardwareIdResetCount = 0);

    /// <summary>Self-service technician registration. The client sends the workstation fingerprint.</summary>
    public sealed record RegisterTechnicianRequest(
        string UserName,
        string Email,
        string Password,
        bool AcceptedTerms,
        string? HardwareId = null);

    public sealed record RegisterTechnicianResponse(
        bool Success,
        string Message,
        bool VerificationEmailSent,
        bool RequiresVerification,
        string? VerificationLink,
        string Email);

    public sealed record ResendVerificationRequest(string Email);

    public sealed record ResendVerificationResponse(bool Sent, string Message, string? VerificationLink);

    public sealed record VerifyEmailResponse(bool Verified, string Message, string? UserName);

    /// <summary>The 6-digit code that accompanies the activation link, typed into the client.</summary>
    public sealed record VerifyEmailCodeRequest(string UserName, string Email, string Code);

    public sealed record SessionResponse(
        AuthTokenDto Tokens,
        UserSummaryDto User,
        IReadOnlyList<string> Permissions,
        LicenseSummaryDto? License,
        string ServerTimeUtc);

    public sealed record MeResponse(
        UserSummaryDto User,
        IReadOnlyList<string> Permissions,
        LicenseSummaryDto? License,
        string? DefaultLanguage,
        string ServerVersion);

}

namespace B_VIP.Contracts.Users
{
    public sealed record UpdateUserRequest(
        string? DisplayName,
        string? Email,
        string? Phone,
        string? Company,
        string? Country);

    public sealed record AdminUpdateUserRequest(
        string? DisplayName,
        string? Email,
        string? Status,
        string? Notes);

    public sealed record AssignRolesRequest(IReadOnlyList<string> Roles);

    public sealed record SuspendUserRequest(string Reason);

    public sealed record UserProfileDto(
        Guid Id,
        string UserName,
        string DisplayName,
        string Email,
        string Status,
        string? Phone,
        string? Company,
        string? Country,
        string? PreferredLanguage,
        IReadOnlyList<string> Roles,
        DateTime CreatedUtc,
        DateTime? LastLoginUtc,
        string? Notes,
        IReadOnlyList<UserLicenseDto> Licenses,
        int OpenTickets,
        int TotalOperations,
        bool EmailVerified = true,
        string? HardwareId = null,
        int HardwareIdResetCount = 0);

    public sealed record UserLicenseDto(
        Guid Id,
        string LicenseKey,
        string PlanName,
        string Status,
        DateTime StartsAt,
        DateTime? ExpiresAt,
        int MaxActivations,
        int CurrentActivations);

    public sealed record CreateUserRequest(
        string UserName,
        string Email,
        string DisplayName,
        string Password,
        IReadOnlyList<string>? Roles = null);

    public sealed record RoleDto(
        Guid Id,
        string Name,
        string? Description,
        bool IsSystem,
        int UserCount,
        IReadOnlyList<string> Permissions,
        DateTime CreatedUtc);

    public sealed record CreateRoleRequest(string Name, string? Description, IReadOnlyList<string>? Permissions);

    public sealed record UpdateRoleRequest(string? Name, string? Description);

    public sealed record SetRolePermissionsRequest(IReadOnlyList<string> Permissions);

    public sealed record PermissionDto(string Name, string Group, string? Description);

    public sealed record SessionDto(
        Guid Id,
        DateTime CreatedUtc,
        DateTime ExpiresUtc,
        DateTime? LastSeenUtc,
        DateTime? RevokedUtc,
        string? IpAddress,
        string? UserAgent,
        bool Active);
}
