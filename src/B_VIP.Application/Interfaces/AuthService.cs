using B_VIP.Contracts.Auth;

namespace B_VIP.Application.Interfaces
{
    public sealed record TokenValidationResult(
        Guid UserId,
        Guid? SessionId,
        string UserName,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions);

    /// <summary>Issues and validates access tokens. The signing key never leaves the server.</summary>
    public interface ITokenProvider
    {
        string CreateAccessToken(Guid userId, Guid? sessionId, string userName, IReadOnlyList<string> roles,
            IReadOnlyList<string> permissions, string? email = null);

        TokenValidationResult? Validate(string accessToken);
    }

    /// <summary>Authentication, sessions, password recovery and the current identity.</summary>
    public interface IAuthService
    {
        /// <summary>
        /// Creates a technician account in the pending-verification state, binds the workstation
        /// fingerprint and sends the activation link.
        /// </summary>
        Task<RegisterTechnicianResponse> RegisterTechnicianAsync(
            RegisterTechnicianRequest request, string? ipAddress, string? correlationId, CancellationToken ct = default);

        /// <summary>Marks the account behind a verification token as verified and active.</summary>
        Task<VerifyEmailResponse> VerifyEmailAsync(string token, string? ipAddress, string? correlationId, CancellationToken ct = default);

        /// <summary>Activates an account from the 6-digit code mailed with the activation link.</summary>
        Task<VerifyEmailResponse> VerifyEmailCodeAsync(VerifyEmailCodeRequest request, string? ipAddress, string? correlationId, CancellationToken ct = default);

        /// <summary>Re-sends the activation link for an account that is still pending.</summary>
        Task<ResendVerificationResponse> ResendVerificationAsync(string email, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<SessionResponse> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, string? correlationId, CancellationToken ct = default);

        Task<SessionResponse> RefreshAsync(string refreshToken, string? ipAddress, string? userAgent, string? correlationId, CancellationToken ct = default);

        Task LogoutAsync(string? refreshToken, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task RevokeAllSessionsAsync(Guid userId, string? correlationId, CancellationToken ct = default);

        Task<SessionResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<(bool Success, string Message)> RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken ct = default);

        Task<(bool Success, string Message)> ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress, CancellationToken ct = default);
    }
}
