using System.Security.Cryptography;
using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Auth;
using B_VIP.Core;
using B_VIP.Core.Entities;
using B_VIP.Core.Rules;
using B_VIP.Core.Security;
using B_VIP.Infrastructure.Data;
using B_VIP.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>
    /// Server-side authentication. Credentials, sessions, refresh tokens and lockouts are all
    /// validated against the database; the desktop client never decides access on its own.
    /// </summary>
    public sealed class AuthService : IAuthService
    {
        private const string GenericLoginError = "invalid-credentials";

        private readonly B_VIPDbContext _db;
        private readonly ITokenProvider _tokens;
        private readonly IAuditLogger _audit;
        private readonly IEmailSender _email;
        private readonly ILicenseService _licenses;
        private readonly AuthOptions _options;
        private readonly EmailOptions _emailOptions;
        private readonly ILogger<AuthService> _logger;

        public AuthService(
            B_VIPDbContext db,
            ITokenProvider tokens,
            IAuditLogger audit,
            IEmailSender email,
            ILicenseService licenses,
            IOptions<AuthOptions> options,
            IOptions<EmailOptions> emailOptions,
            ILogger<AuthService> logger)
        {
            _db = db;
            _tokens = tokens;
            _audit = audit;
            _email = email;
            _licenses = licenses;
            _options = options.Value;
            _emailOptions = emailOptions.Value;
            _logger = logger;
        }

        public async Task<SessionResponse> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.UserName) || string.IsNullOrWhiteSpace(request.Password))
            {
                throw new B_VIP.Core.Common.DomainException("invalid-request", "User name and password are required.", 400);
            }

            DateTime now = DateTime.UtcNow;
            string identifier = request.UserName.Trim();

            if (await IsLoginThrottledAsync(identifier, ipAddress, now, ct))
            {
                await _audit.LogAsync(null, identifier, "auth.login", "user", identifier, AuditOutcome.Denied,
                    "Too many failed attempts.", ipAddress, correlationId, ct);
                throw new B_VIP.Core.Common.DomainException("too-many-attempts", "Too many failed attempts. Try again later.", 429);
            }

            string normalized = ValidationRules.NormalizeUserName(identifier);
            string normalizedEmail = ValidationRules.NormalizeEmail(identifier);

            User? user = await _db.Users
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => !u.IsDeleted &&
                                           (u.NormalizedUserName == normalized || u.NormalizedEmail == normalizedEmail), ct);

            bool passwordOk = user is not null && PasswordHasher.Verify(request.Password, user.PasswordHash);

            if (user is null || !passwordOk)
            {
                await RegisterFailedAttemptAsync(user, identifier, ipAddress, correlationId, ct);
                await _audit.LogAsync(user?.Id, identifier, "auth.login", "user", user?.Id.ToString() ?? identifier,
                    AuditOutcome.Failure, "Invalid username or password.", ipAddress, correlationId, ct);
                throw new B_VIP.Core.Common.DomainException(GenericLoginError, "Invalid username or password.", 401);
            }

            if (user.LockoutEndUtc.HasValue && user.LockoutEndUtc.Value > now)
            {
                await _audit.LogAsync(user.Id, user.UserName, "auth.login", "user", user.Id.ToString(),
                    AuditOutcome.Denied, "Account locked.", ipAddress, correlationId, ct);
                throw new B_VIP.Core.Common.DomainException("account-locked",
                    $"Account is locked until {user.LockoutEndUtc:u} after repeated failed attempts.", 423);
            }

            if (!user.EmailConfirmed || user.Status == AccountStatus.PendingVerification)
            {
                await _audit.LogAsync(user.Id, user.UserName, "auth.login", "user", user.Id.ToString(),
                    AuditOutcome.Denied, "E-mail address is not verified.", ipAddress, correlationId, ct);
                throw new B_VIP.Core.Common.DomainException("email-not-verified",
                    "This account still has to be activated. Open the activation link sent to the registered e-mail address.", 403);
            }

            if (user.Status != AccountStatus.Active)
            {
                await _audit.LogAsync(user.Id, user.UserName, "auth.login", "user", user.Id.ToString(),
                    AuditOutcome.Denied, $"Account status {user.Status}.", ipAddress, correlationId, ct);
                throw new B_VIP.Core.Common.DomainException("account-suspended", "This account is suspended.", 403);
            }

            await BindHardwareIdAsync(user, request.DeviceFingerprint, ipAddress, correlationId, ct);

            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            user.LastLoginUtc = now;

            UserSession session = new()
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                SessionTokenHash = TokenFactory.HashToken(TokenFactory.CreateSecureToken()),
                CreatedUtc = now,
                ExpiresUtc = now.AddMinutes(Math.Max(_options.AccessTokenMinutes, 30) * 2),
                LastSeenUtc = now,
                IpAddress = ipAddress,
                UserAgent = Truncate(userAgent, 512)
            };

            string refreshToken = TokenFactory.CreateSecureToken();
            RefreshToken refresh = new()
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                SessionId = session.Id,
                TokenHash = TokenFactory.HashToken(refreshToken),
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(_options.RefreshTokenDays),
                IpAddress = ipAddress
            };

            _db.UserSessions.Add(session);
            _db.RefreshTokens.Add(refresh);

            _db.LoginAttempts.Add(new LoginAttempt
            {
                Id = Guid.NewGuid(),
                UserName = Truncate(identifier, 64) ?? string.Empty,
                UserId = user.Id,
                Succeeded = true,
                Reason = "password",
                IpAddress = ipAddress,
                OccurredUtc = now,
                CorrelationId = correlationId
            });

            await _db.SaveChangesAsync(ct);

            (IReadOnlyList<string> roles, IReadOnlyList<string> permissions) = await ResolveAuthorizationAsync(user.Id, ct);
            LicenseSummaryDto? license = await GetLicenseSummaryAsync(user.Id, ct);

            string accessToken = _tokens.CreateAccessToken(user.Id, session.Id, user.UserName, roles, permissions, user.Email);

            await _audit.LogAsync(user.Id, user.UserName, "auth.login", "user", user.Id.ToString(),
                AuditOutcome.Success, "Password login succeeded.", ipAddress, correlationId, ct);

            _logger.LogInformation("Login succeeded for {UserName} ({UserId})", user.UserName, user.Id);

            return new SessionResponse(
                new AuthTokenDto(accessToken, refreshToken, session.ExpiresUtc, refresh.ExpiresUtc),
                MapSummary(user, roles),
                permissions,
                license,
                now.ToString("o"));
        }

        public async Task<SessionResponse> RefreshAsync(string refreshToken, string? ipAddress, string? userAgent, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new B_VIP.Core.Common.DomainException("invalid-request", "Refresh token is required.", 400);
            }

            string hash = TokenFactory.HashToken(refreshToken);
            RefreshToken? stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            DateTime now = DateTime.UtcNow;

            if (stored is null || !stored.IsActive(now))
            {
                await _audit.LogAsync(stored?.UserId, null, "auth.refresh", "refresh-token",
                    stored?.Id.ToString(), AuditOutcome.Failure, "Refresh token rejected.", ipAddress, correlationId, ct);
                throw new B_VIP.Core.Common.DomainException("invalid-refresh-token", "The session has expired. Sign in again.", 401);
            }

            User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId && !u.IsDeleted, ct);
            if (user is null || user.Status != AccountStatus.Active)
            {
                throw new B_VIP.Core.Common.DomainException("account-suspended", "This account is no longer active.", 403);
            }

            UserSession? session = stored.SessionId.HasValue
                ? await _db.UserSessions.FirstOrDefaultAsync(s => s.Id == stored.SessionId, ct)
                : null;

            if (session is not null && !session.IsActive(now))
            {
                throw new B_VIP.Core.Common.DomainException("session-expired", "The session has expired. Sign in again.", 401);
            }

            // Rotation: the used token is revoked and replaced.
            string newRefreshToken = TokenFactory.CreateSecureToken();
            RefreshToken replacement = new()
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                SessionId = stored.SessionId,
                TokenHash = TokenFactory.HashToken(newRefreshToken),
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(_options.RefreshTokenDays),
                IpAddress = ipAddress
            };

            stored.RevokedUtc = now;
            stored.ReplacedByTokenId = replacement.Id;
            _db.RefreshTokens.Add(replacement);

            if (session is not null)
            {
                session.LastSeenUtc = now;
                session.ExpiresUtc = now.AddMinutes(Math.Max(_options.AccessTokenMinutes, 30) * 2);
            }

            await _db.SaveChangesAsync(ct);

            (IReadOnlyList<string> roles, IReadOnlyList<string> permissions) = await ResolveAuthorizationAsync(user.Id, ct);
            LicenseSummaryDto? license = await GetLicenseSummaryAsync(user.Id, ct);
            string accessToken = _tokens.CreateAccessToken(user.Id, session?.Id, user.UserName, roles, permissions, user.Email);

            return new SessionResponse(
                new AuthTokenDto(accessToken, newRefreshToken, session?.ExpiresUtc ?? now.AddHours(2), replacement.ExpiresUtc),
                MapSummary(user, roles),
                permissions,
                license,
                now.ToString("o"));
        }

        public async Task LogoutAsync(string? refreshToken, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                string hash = TokenFactory.HashToken(refreshToken);
                RefreshToken? stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
                if (stored is not null && stored.RevokedUtc is null)
                {
                    stored.RevokedUtc = now;
                    userId ??= stored.UserId;

                    if (stored.SessionId.HasValue)
                    {
                        UserSession? session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Id == stored.SessionId, ct);
                        if (session is not null) session.RevokedUtc = now;
                    }

                    await _db.SaveChangesAsync(ct);
                }
            }

            if (userId.HasValue)
            {
                await _audit.LogAsync(userId, null, "auth.logout", "user", userId.ToString(),
                    AuditOutcome.Success, "Session ended.", ipAddress, correlationId, ct);
            }
        }

        public async Task RevokeAllSessionsAsync(Guid userId, string? correlationId, CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;

            List<UserSession> sessions = await _db.UserSessions
                .Where(s => s.UserId == userId && s.RevokedUtc == null)
                .ToListAsync(ct);
            foreach (UserSession session in sessions) session.RevokedUtc = now;

            List<RefreshToken> tokens = await _db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedUtc == null)
                .ToListAsync(ct);
            foreach (RefreshToken token in tokens) token.RevokedUtc = now;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "auth.revoke-sessions", "user", userId.ToString(),
                AuditOutcome.Success, $"Revoked {sessions.Count} sessions and {tokens.Count} refresh tokens.", null, correlationId, ct);
        }

        public async Task<SessionResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            User user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
                ?? throw new B_VIP.Core.Common.DomainException("user-not-found", "User not found.", 404);

            if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
            {
                await _audit.LogAsync(userId, user.UserName, "auth.change-password", "user", userId.ToString(),
                    AuditOutcome.Failure, "Current password mismatch.", ipAddress, correlationId, ct);
                throw new B_VIP.Core.Common.DomainException("invalid-password", "The current password is incorrect.", 400);
            }

            (bool valid, string? error) = ValidationRules.ValidatePasswordStrength(request.NewPassword);
            if (!valid)
            {
                throw new B_VIP.Core.Common.DomainException(error!, "The new password does not meet the policy.", 400);
            }

            user.PasswordHash = PasswordHasher.Hash(request.NewPassword!);
            user.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await RevokeAllSessionsAsync(userId, correlationId, ct);

            return await LoginAsync(
                new LoginRequest(user.UserName, request.NewPassword!),
                ipAddress, null, correlationId, ct);
        }

        public async Task<(bool Success, string Message)> RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;

            int recent = await _db.PasswordResetRequests.CountAsync(r =>
                r.RequestedIp == ipAddress && r.CreatedUtc > now.AddHours(-1), ct);

            // Always answer the same way so the endpoint cannot be used to enumerate accounts.
            if (recent >= _options.PasswordResetPerEmailPerHour)
            {
                return (false, "too-many-requests");
            }

            string normalized = ValidationRules.NormalizeEmail(email ?? string.Empty);
            User? user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized && !u.IsDeleted, ct);

            if (user is not null && user.Status == AccountStatus.Active)
            {
                string token = TokenFactory.CreateSecureToken();
                _db.PasswordResetRequests.Add(new PasswordResetRequest
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    TokenHash = TokenFactory.HashToken(token),
                    CreatedUtc = now,
                    ExpiresUtc = now.AddMinutes(_options.PasswordResetMinutes),
                    RequestedIp = ipAddress
                });
                await _db.SaveChangesAsync(ct);

                await _audit.LogAsync(user.Id, user.UserName, "auth.password-reset-request", "user", user.Id.ToString(),
                    AuditOutcome.Success, "Reset token issued (delivery depends on the configured e-mail integration).",
                    ipAddress, null, ct);
            }

            return (true, "If the address exists, a reset request has been recorded.");
        }

        public async Task<(bool Success, string Message)> ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return (false, "invalid-request");
            }

            (bool strong, string? policyError) = ValidationRules.ValidatePasswordStrength(request.NewPassword);
            if (!strong)
            {
                return (false, policyError ?? "password-policy");
            }

            string hash = TokenFactory.HashToken(request.Token);
            PasswordResetRequest? reset = await _db.PasswordResetRequests.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
            DateTime now = DateTime.UtcNow;

            if (reset is null || reset.UsedUtc.HasValue || reset.ExpiresUtc <= now)
            {
                return (false, "reset-token-invalid");
            }

            User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == reset.UserId && !u.IsDeleted, ct);
            if (user is null || user.Status != AccountStatus.Active)
            {
                return (false, "account-not-active");
            }

            user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            user.UpdatedUtc = now;
            reset.UsedUtc = now;

            await _db.SaveChangesAsync(ct);
            await RevokeAllSessionsAsync(user.Id, null, ct);

            await _audit.LogAsync(user.Id, user.UserName, "auth.password-reset", "user", user.Id.ToString(),
                AuditOutcome.Success, "Password reset completed.", ipAddress, null, ct);

            return (true, "password-reset");
        }

        // ----------------------------------------------------------------- practitioner onboarding

        public async Task<RegisterTechnicianResponse> RegisterTechnicianAsync(
            RegisterTechnicianRequest request, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!request.AcceptedTerms)
            {
                throw new B_VIP.Core.Common.DomainException("terms-not-accepted",
                    "The terms of service have to be accepted before an account can be created.", 400);
            }

            string userName = (request.UserName ?? string.Empty).Trim();
            string email = (request.Email ?? string.Empty).Trim();

            if (userName.Length < 4 || !ValidationRules.IsValidUserName(userName))
            {
                throw new B_VIP.Core.Common.DomainException("invalid-user-name",
                    "The username needs at least 4 alphanumeric characters.", 400);
            }

            if (!ValidationRules.IsValidEmail(email))
            {
                throw new B_VIP.Core.Common.DomainException("invalid-email", "The e-mail address is not valid.", 400);
            }

            (bool strong, string? policyError) = ValidationRules.ValidatePasswordStrength(request.Password ?? string.Empty);
            if (!strong)
            {
                throw new B_VIP.Core.Common.DomainException("weak-password",
                    policyError ?? "The password does not satisfy the password policy.", 400);
            }

            string normalizedUser = ValidationRules.NormalizeUserName(userName);
            string normalizedEmail = ValidationRules.NormalizeEmail(email);

            bool exists = await _db.Users.AnyAsync(u =>
                !u.IsDeleted && (u.NormalizedUserName == normalizedUser || u.NormalizedEmail == normalizedEmail), ct);

            if (exists)
            {
                throw new B_VIP.Core.Common.DomainException("account-exists",
                    "A technician account with this username or e-mail address already exists.", 409);
            }

            Role? technicianRole = await _db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == RoleNames.User.ToUpperInvariant(), ct)
                ?? throw new B_VIP.Core.Common.DomainException("role-not-found",
                    "The technician role is not seeded on this server.", 500);

            DateTime now = DateTime.UtcNow;
            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                NormalizedUserName = normalizedUser,
                Email = email,
                NormalizedEmail = normalizedEmail,
                DisplayName = userName,
                PasswordHash = PasswordHasher.Hash(request.Password!),
                Status = AccountStatus.PendingVerification,
                EmailConfirmed = false,
                HardwareId = NormalizeHardwareId(request.HardwareId),
                CreatedUtc = now,
                UpdatedUtc = now
            };

            user.Profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RegisteredUtc = now,
                UpdatedUtc = now
            };

            user.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = technicianRole.Id,
                AssignedUtc = now
            });

            string token = TokenFactory.CreateSecureToken();
            string code = CreateVerificationCode();
            user.EmailVerificationTokenHash = TokenFactory.HashToken(token);
            user.EmailVerificationSentUtc = now;
            user.EmailVerificationCodeHash = TokenFactory.HashToken(code);
            user.EmailVerificationCodeExpiresUtc = now.AddMinutes(VerificationCodeMinutes);

            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(user.Id, user.UserName, "auth.register", "user", user.Id.ToString(),
                AuditOutcome.Success, "Technician self-registration completed; awaiting e-mail verification.",
                ipAddress, correlationId, ct);

            string link = BuildVerificationLink(token);

            // The row and the 6-digit code are committed, so delivery must never delay (or fail)
            // the registration response: it runs in the background with a five second budget.
            QueueVerificationEmail(user.Email, code, correlationId);

            _logger.LogInformation("Technician {UserName} registered; verification code queued for delivery.",
                user.UserName);

            return new RegisterTechnicianResponse(
                true,
                "Account created. Verification code sent.",
                VerificationEmailSent: true,
                RequiresVerification: true,
                VerificationLink: _emailOptions.ExposeVerificationLink ? link : null,
                Email: user.Email);
        }

        public async Task<VerifyEmailResponse> VerifyEmailAsync(string token, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new VerifyEmailResponse(false, "The activation link is incomplete.", null);
            }

            string hash = TokenFactory.HashToken(token.Trim());
            User? user = await _db.Users.FirstOrDefaultAsync(u => !u.IsDeleted && u.EmailVerificationTokenHash == hash, ct);

            if (user is null)
            {
                return new VerifyEmailResponse(false,
                    "The activation link is invalid or was already used.", null);
            }

            DateTime now = DateTime.UtcNow;
            int hours = _emailOptions.VerificationTokenHours <= 0 ? 48 : _emailOptions.VerificationTokenHours;

            if (user.EmailVerificationSentUtc.HasValue && user.EmailVerificationSentUtc.Value.AddHours(hours) <= now)
            {
                return new VerifyEmailResponse(false,
                    "The activation link has expired. Use 'Resend verification e-mail' to obtain a new one.", user.UserName);
            }

            user.EmailConfirmed = true;
            if (user.Status == AccountStatus.PendingVerification)
            {
                user.Status = AccountStatus.Active;
            }

            user.EmailVerificationTokenHash = null;
            user.EmailVerificationSentUtc = null;
            user.EmailVerificationCodeHash = null;
            user.EmailVerificationCodeExpiresUtc = null;
            user.UpdatedUtc = now;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(user.Id, user.UserName, "auth.email-verified", "user", user.Id.ToString(),
                AuditOutcome.Success, "E-mail address verified; account activated.", ipAddress, correlationId, ct);

            // Activation includes the free trial: full access for the next 72 hours.
            await GrantTrialSafeAsync(user.Id, correlationId, ct);

            return new VerifyEmailResponse(true,
                $"Your e-mail address is verified. Your {LicenseRules.TrialDays}-day free trial is active. You can sign in now.",
                user.UserName);
        }

        /// <summary>
        /// Grants the trial after activation. A failure here must not undo a verified address, so
        /// the exception is logged and reported as a warning instead of failing the request.
        /// </summary>
        private async Task GrantTrialSafeAsync(Guid userId, string? correlationId, CancellationToken ct)
        {
            try
            {
                await _licenses.EnsureTrialAsync(userId, correlationId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The free trial could not be granted to user {UserId}. An administrator can issue a license manually.", userId);
            }
        }

        public async Task<ResendVerificationResponse> ResendVerificationAsync(
            string email, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email) || !ValidationRules.IsValidEmail(email))
            {
                throw new B_VIP.Core.Common.DomainException("invalid-email", "The e-mail address is not valid.", 400);
            }

            string normalized = ValidationRules.NormalizeEmail(email);
            User? user = await _db.Users.FirstOrDefaultAsync(u =>
                !u.IsDeleted && u.NormalizedEmail == normalized && !u.EmailConfirmed, ct);

            // The answer is deliberately identical for unknown and known addresses.
            const string generic = "If this address belongs to an account that is still pending, a new activation link is on its way.";

            if (user is null)
            {
                return new ResendVerificationResponse(true, generic, null);
            }

            DateTime now = DateTime.UtcNow;
            DateTime windowStart = now.AddMinutes(-1);
            if (user.EmailVerificationSentUtc.HasValue && user.EmailVerificationSentUtc.Value > windowStart)
            {
                throw new B_VIP.Core.Common.DomainException("verification-throttled",
                    "A verification e-mail was sent moments ago. Wait a minute and try again.", 429);
            }

            string token = TokenFactory.CreateSecureToken();
            string code = CreateVerificationCode();
            user.EmailVerificationTokenHash = TokenFactory.HashToken(token);
            user.EmailVerificationSentUtc = now;
            user.EmailVerificationCodeHash = TokenFactory.HashToken(code);
            user.EmailVerificationCodeExpiresUtc = now.AddMinutes(VerificationCodeMinutes);
            user.UpdatedUtc = now;
            await _db.SaveChangesAsync(ct);

            string link = BuildVerificationLink(token);
            QueueVerificationEmail(user.Email, code, correlationId);

            await _audit.LogAsync(user.Id, user.UserName, "auth.verification-resent", "user", user.Id.ToString(),
                AuditOutcome.Success, "Activation e-mail re-sent.", ipAddress, correlationId, ct);

            return new ResendVerificationResponse(true, generic,
                _emailOptions.ExposeVerificationLink ? link : null);
        }

        /// <summary>
        /// Binds the workstation on first sign-in. A different machine is refused for technicians;
        /// the master administrator and every administrator role are exempt, so the management
        /// console can be opened from any PC. An administrator decouples an account, after which
        /// the next sign-in binds that account to the new workstation.
        /// </summary>
        private async Task BindHardwareIdAsync(User user, string? fingerprint, string? ipAddress, string? correlationId, CancellationToken ct)
        {
            if (IsAdministrator(user))
            {
                return;
            }

            string? provided = NormalizeHardwareId(fingerprint);
            string? bound = NormalizeHardwareId(user.HardwareId);

            if (string.IsNullOrEmpty(bound))
            {
                // First sign-in binds this workstation. A request without a fingerprint leaves the
                // account unbound; it binds on the next successful sign-in that carries one.
                if (provided is null)
                {
                    return;
                }

                user.HardwareId = provided;
                user.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                await _audit.LogAsync(user.Id, user.UserName, "auth.hwid-bound", "user", user.Id.ToString(),
                    AuditOutcome.Success, "Workstation hardware ID bound on first sign-in.", ipAddress, correlationId, ct);
                return;
            }

            if (provided is not null && string.Equals(bound, provided, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Either another workstation or a request that omits the fingerprint: both are refused
            // once the account is bound, so the check cannot be skipped by leaving the value out.
            await _audit.LogAsync(user.Id, user.UserName, "auth.hwid-mismatch", "user", user.Id.ToString(),
                AuditOutcome.Denied, "Sign-in attempted from an unregistered workstation.", ipAddress, correlationId, ct);

            throw new B_VIP.Core.Common.DomainException("device-mismatch",
                "This account is bound to another computer. Please contact administrator to reset HWID.", 403);
        }

        /// <summary>
        /// Administrator accounts are never tied to one machine: the shipped master administrator,
        /// the Administrator role and any role carrying "admin" in its name sign in from anywhere.
        /// </summary>
        private static bool IsAdministrator(User user)
        {
            if (string.Equals(user.Email, DataSeeder.MasterAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return user.UserRoles.Any(ur =>
                ur.Role is not null &&
                (ur.Role.Name.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                 ur.Role.NormalizedName.Contains("ADMIN", StringComparison.Ordinal)));
        }


        private string BuildVerificationLink(string token)
        {
            string baseUrl = string.IsNullOrWhiteSpace(_emailOptions.PublicBaseUrl)
                ? "https://b-vip.onrender.com"
                : _emailOptions.PublicBaseUrl.TrimEnd('/');

            return $"{baseUrl}/api/auth/verify?token={Uri.EscapeDataString(token)}";
        }

        /// <summary>Subject of the activation mail, kept in one place so tests can assert it.</summary>
        public const string VerificationSubject = "[B_VIP Tool] Account Verification Code";

        /// <summary>How long the background send may take before it is abandoned.</summary>
        public const int VerificationDeliveryMilliseconds = 5000;

        /// <summary>
        /// Sends the activation mail away from the HTTP request. The relay gets five seconds;
        /// every failure is logged and swallowed, because the caller's account and code are
        /// already committed and the request must not wait for a mailbox.
        /// </summary>
        private void QueueVerificationEmail(string email, string code, string? correlationId)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using CancellationTokenSource budget = new(TimeSpan.FromMilliseconds(VerificationDeliveryMilliseconds));
                try
                {
                    bool sent = await SendVerificationEmailAsync(email, code, budget.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Activation e-mail for {Email}: {State}. Correlation={CorrelationId}",
                        email, sent ? "sent" : "not delivered", correlationId);
                }
                catch (Exception ex)
                {
                    // Deliberately swallowed: registration has already succeeded and the code in
                    // the database is what the technician types in.
                    _logger.LogWarning(ex,
                        "Activation e-mail for {Email} could not be sent within {Seconds} seconds. Correlation={CorrelationId}",
                        email, VerificationDeliveryMilliseconds / 1000, correlationId);
                }
            });
        }

        /// <summary>
        /// Builds and sends the approved English activation mail: the 6-digit code, the strict
        /// 15-minute validity and the never-share rule.
        /// </summary>
        private async Task<bool> SendVerificationEmailAsync(string email, string code, CancellationToken ct)
        {
            string text =
                "Welcome to B_VIP Tool,\n\n" +
                "Thank you for registering on our smart device maintenance and servicing platform.\n" +
                "To complete your account activation and start your 3-day free trial, use the following verification code inside the tool:\n\n" +
                $"{code}\n\n" +
                $"Validity: This code is valid for {VerificationCodeMinutes} minutes only.\n" +
                "Security: The B_VIP support team will never ask for this code. Do not share it with anyone.\n" +
                "If you did not request an account on B_VIP Tool, you can safely ignore this email; no action will be taken on your data.\n\n" +
                "B_VIP Tool Team\n" +
                "All-in-One Solution for Smart Device Servicing & Maintenance.";

            string html =
                "<div style=\"font-family:'Segoe UI',Arial,sans-serif;color:#E1E1E1;background:#0B132B;padding:24px;border-radius:8px;\">" +
                "<p>Welcome to <strong>B_VIP Tool</strong>,</p>" +
                "<p>Thank you for registering on our smart device maintenance and servicing platform.<br />" +
                "To complete your account activation and start your 3-day free trial, use the following verification code inside the tool:</p>" +
                $"<p style=\"font-size:26px;letter-spacing:8px;font-weight:700;color:#00D2FF;\">{code}</p>" +
                $"<p><strong>Validity:</strong> This code is valid for {VerificationCodeMinutes} minutes only.<br />" +
                "<strong>Security:</strong> The B_VIP support team will never ask for this code. Do not share it with anyone.<br />" +
                "If you did not request an account on B_VIP Tool, you can safely ignore this email; no action will be taken on your data.</p>" +
                "<p>B_VIP Tool Team<br /><em>All-in-One Solution for Smart Device Servicing &amp; Maintenance.</em></p></div>";

            return await _email.SendAsync(new EmailMessage(email, VerificationSubject, text, html), ct);
        }

        /// <summary>How long the 6-digit activation code is accepted.</summary>
        public const int VerificationCodeMinutes = 15;

        private static string CreateVerificationCode()
            => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Activates an account from the 6-digit code mailed with the activation link. The code is
        /// compared by hash and expires after <see cref="VerificationCodeMinutes"/> minutes, so a
        /// leaked mailbox preview or a guessed code cannot activate the account later.
        /// </summary>
        public async Task<VerifyEmailResponse> VerifyEmailCodeAsync(
            VerifyEmailCodeRequest request, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            string code = (request.Code ?? string.Empty).Trim();
            string email = (request.Email ?? string.Empty).Trim();
            string userName = (request.UserName ?? string.Empty).Trim();

            if (code.Length != 6 || !code.All(char.IsAsciiDigit) || !ValidationRules.IsValidEmail(email))
            {
                return new VerifyEmailResponse(false, "Enter the 6-digit code and the e-mail address it was sent to.", null);
            }

            string normalizedEmail = ValidationRules.NormalizeEmail(email);
            string normalizedUser = ValidationRules.NormalizeUserName(userName);

            User? user = await _db.Users.FirstOrDefaultAsync(u =>
                !u.IsDeleted && u.NormalizedEmail == normalizedEmail && u.EmailVerificationCodeHash != null, ct);

            if (user is null || (normalizedUser.Length > 0 && user.NormalizedUserName != normalizedUser))
            {
                return new VerifyEmailResponse(false, "The code does not match a pending account.", null);
            }

            DateTime now = DateTime.UtcNow;
            if (user.EmailVerificationCodeExpiresUtc is null || user.EmailVerificationCodeExpiresUtc.Value <= now)
            {
                return new VerifyEmailResponse(false,
                    "The code has expired. Use 'Resend verification e-mail' to obtain a new one.", user.UserName);
            }

            string hash = TokenFactory.HashToken(code);
            if (!string.Equals(user.EmailVerificationCodeHash, hash, StringComparison.Ordinal))
            {
                await _audit.LogAsync(user.Id, user.UserName, "auth.code-rejected", "user", user.Id.ToString(),
                    AuditOutcome.Denied, "A 6-digit activation code did not match.", ipAddress, correlationId, ct);
                return new VerifyEmailResponse(false, "That code is not correct. Check the message and try again.", null);
            }

            user.EmailConfirmed = true;
            if (user.Status == AccountStatus.PendingVerification)
            {
                user.Status = AccountStatus.Active;
            }

            user.EmailVerificationTokenHash = null;
            user.EmailVerificationSentUtc = null;
            user.EmailVerificationCodeHash = null;
            user.EmailVerificationCodeExpiresUtc = null;
            user.UpdatedUtc = now;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(user.Id, user.UserName, "auth.email-verified", "user", user.Id.ToString(),
                AuditOutcome.Success, "E-mail address verified with the 6-digit code; account activated.",
                ipAddress, correlationId, ct);

            // Activation always carries the free trial, whichever path (link or code) verified it.
            await GrantTrialSafeAsync(user.Id, correlationId, ct);

            return new VerifyEmailResponse(true,
                $"Your e-mail address is verified. Your {LicenseRules.TrialDays}-day free trial is active. You can sign in now.",
                user.UserName);
        }

        private static string? NormalizeHardwareId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string trimmed = value.Trim().ToUpperInvariant();
            return trimmed.Length <= 128 ? trimmed : trimmed[..128];
        }

        internal static UserSummaryDto MapSummary(User user, IReadOnlyList<string> roles) => new(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.Email,
            user.Status.ToString(),
            roles,
            user.CreatedUtc,
            user.LastLoginUtc,
            user.EmailConfirmed,
            user.HardwareId,
            user.HardwareIdResetCount);

        internal Task<(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions)> ResolveAuthorizationAsync(Guid userId, CancellationToken ct)
            => AuthorizationResolver.ResolveAsync(_db, userId, ct);

        internal async Task<LicenseSummaryDto?> GetLicenseSummaryAsync(Guid userId, CancellationToken ct)
        {
            DateTime now = DateTime.UtcNow;
            License? license = await _db.Licenses
                .Include(l => l.LicensePlan)
                .Include(l => l.Entitlements)
                .Include(l => l.Activations)
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.CreatedUtc)
                .FirstOrDefaultAsync(ct);

            if (license is null) return null;

            Core.LicenseStatus status = Core.Rules.LicenseRules.EffectiveStatus(license, now);
            return new LicenseSummaryDto(
                license.Id,
                license.LicenseKey,
                license.LicensePlan.Code,
                license.LicensePlan.Name,
                status.ToString(),
                license.StartsAt,
                license.ExpiresAt,
                license.MaxActivations,
                license.Activations.Count(a => a.DeactivatedUtc is null),
                license.Entitlements.Where(e => e.Enabled).Select(e => e.FeatureKey).ToList());
        }

        private async Task<bool> IsLoginThrottledAsync(string identifier, string? ipAddress, DateTime now, CancellationToken ct)
        {
            DateTime windowStart = now.AddMinutes(-15);
            int failures = await _db.LoginAttempts.CountAsync(a =>
                !a.Succeeded &&
                a.OccurredUtc > windowStart &&
                (a.UserName == identifier || (ipAddress != null && a.IpAddress == ipAddress)), ct);

            return failures >= Math.Max(_options.MaxFailedAccessAttempts * 2, 10);
        }

        private async Task RegisterFailedAttemptAsync(User? user, string identifier, string? ipAddress, string? correlationId, CancellationToken ct)
        {
            _db.LoginAttempts.Add(new LoginAttempt
            {
                Id = Guid.NewGuid(),
                UserName = Truncate(identifier, 64) ?? string.Empty,
                UserId = user?.Id,
                Succeeded = false,
                Reason = user is null ? "unknown-user" : "bad-password",
                IpAddress = ipAddress,
                OccurredUtc = DateTime.UtcNow,
                CorrelationId = correlationId
            });

            if (user is not null)
            {
                user.AccessFailedCount++;
                if (user.AccessFailedCount >= _options.MaxFailedAccessAttempts)
                {
                    user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(_options.LockoutMinutes);
                    user.AccessFailedCount = 0;
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }
}
