using System.Text.RegularExpressions;
using B_VIP.Core.Entities;

namespace B_VIP.Core.Rules
{
    /// <summary>Pure licensing rules shared by the backend and the desktop client.</summary>
    public static class LicenseRules
    {
        /// <summary>Resolves the effective status of a license at a given instant.</summary>
        public static LicenseStatus EffectiveStatus(License license, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(license);

            return license.Status switch
            {
                LicenseStatus.Revoked => LicenseStatus.Revoked,
                LicenseStatus.Suspended => LicenseStatus.Suspended,
                LicenseStatus.Invalid => LicenseStatus.Invalid,
                LicenseStatus.Pending => LicenseStatus.Pending,
                LicenseStatus.Expired => LicenseStatus.Expired,
                LicenseStatus.Active => IsPastExpiry(license, utcNow) ? LicenseStatus.Expired : LicenseStatus.Active,
                _ => LicenseStatus.Invalid
            };
        }

        public static bool IsPastExpiry(License license, DateTime utcNow)
            => license.ExpiresAt.HasValue && license.ExpiresAt.Value <= utcNow;

        public static bool IsActive(License license, DateTime utcNow)
            => EffectiveStatus(license, utcNow) == LicenseStatus.Active;

        /// <summary>Determines whether a new activation is allowed for the license.</summary>
        public static bool CanActivate(License license, DateTime utcNow, out string? reason)
        {
            LicenseStatus status = EffectiveStatus(license, utcNow);
            if (status != LicenseStatus.Active)
            {
                reason = $"license-{status.ToString().ToLowerInvariant()}";
                return false;
            }

            int activeCount = license.Activations.Count(a => a.DeactivatedUtc is null);
            if (activeCount >= license.MaxActivations)
            {
                reason = "activation-limit-reached";
                return false;
            }

            reason = null;
            return true;
        }

        public static VerificationResult ToVerificationResult(LicenseStatus status) => status switch
        {
            LicenseStatus.Active => VerificationResult.Valid,
            LicenseStatus.Expired => VerificationResult.Expired,
            LicenseStatus.Suspended => VerificationResult.Suspended,
            LicenseStatus.Revoked => VerificationResult.Revoked,
            LicenseStatus.Invalid => VerificationResult.InvalidRequest,
            LicenseStatus.Pending => VerificationResult.Pending,
            _ => VerificationResult.InvalidRequest
        };

        public static DateTime? ComputeExpiry(DateTime startsAt, int? durationDays, bool lifetime)
            => lifetime || durationDays is null or <= 0 ? null : startsAt.AddDays(durationDays.Value);

        /// <summary>The tier code every new technician receives when the e-mail address is verified.</summary>
        public const string TrialPlanCode = "trial";

        /// <summary>Full access for three days (72 hours) from the moment the account is activated.</summary>
        public const int TrialDays = 3;

        public static DateTime TrialExpiry(DateTime activatedUtc) => activatedUtc.AddDays(TrialDays);

        /// <summary>
        /// Validates a subscription tier before it is stored. Returns the normalized code and the
        /// reason the tier was rejected, so the API can answer with a precise message.
        /// </summary>
        public static (string Code, string? Error) ValidatePlan(
            string? code, string? name, int? durationDays, int maxActivations, bool lifetime, decimal? price)
        {
            string normalized = (code ?? string.Empty).Trim().ToLowerInvariant();

            if (normalized.Length is < 2 or > 40 || !normalized.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            {
                return (normalized, "The plan code needs 2-40 letters, digits, '-' or '_'.");
            }

            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
            {
                return (normalized, "The plan needs a name of at least 2 characters.");
            }

            if (!lifetime && durationDays is null or <= 0)
            {
                return (normalized, "A non-lifetime plan needs a positive duration in days.");
            }

            if (durationDays is > 3650)
            {
                return (normalized, "The duration cannot exceed 3650 days.");
            }

            if (maxActivations is < 1 or > 1000)
            {
                return (normalized, "The activation limit must be between 1 and 1000 workstations.");
            }

            if (price.HasValue && price.Value < 0)
            {
                return (normalized, "The price cannot be negative.");
            }

            return (normalized, null);
        }

        /// <summary>
        /// VIP activation key in the <c>BVIP-XXXX-XXXX-XXXX</c> format, built from an unambiguous
        /// alphabet (no I, O, 0 or 1) so a key can be read out over the phone.
        /// </summary>
        public static string GenerateLicenseKey()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

            Span<byte> bytes = stackalloc byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

            Span<char> body = stackalloc char[12];
            for (int i = 0; i < body.Length; i++)
            {
                body[i] = alphabet[bytes[i] % alphabet.Length];
            }

            return $"BVIP-{new string(body[..4])}-{new string(body[4..8])}-{new string(body[8..])}";
        }

        public static bool IsFeatureEnabled(License license, string featureKey)
        {
            LicenseFeatureEntitlement? entitlement = license.Entitlements
                .FirstOrDefault(e => string.Equals(e.FeatureKey, featureKey, StringComparison.OrdinalIgnoreCase));
            return entitlement?.Enabled == true;
        }
    }

    /// <summary>Operation state machine: transitions are validated before they are persisted.</summary>
    public static class OperationRules
    {
        private static readonly Dictionary<OperationStatus, OperationStatus[]> Allowed = new()
        {
            [OperationStatus.Queued] = [OperationStatus.Validating, OperationStatus.Cancelled, OperationStatus.Failed],
            // A fast tool can finish before the client reports that it started running, so a result
            // may arrive while the operation is still Validating.
            [OperationStatus.Validating] =
            [
                OperationStatus.Running, OperationStatus.Completed, OperationStatus.Failed,
                OperationStatus.Cancelled, OperationStatus.TimedOut, OperationStatus.RequiresAttention
            ],
            [OperationStatus.Running] = [OperationStatus.Completed, OperationStatus.Failed, OperationStatus.Cancelled, OperationStatus.TimedOut, OperationStatus.RequiresAttention],
            [OperationStatus.Completed] = [],
            [OperationStatus.Failed] = [],
            [OperationStatus.Cancelled] = [],
            [OperationStatus.TimedOut] = [],
            [OperationStatus.RequiresAttention] = [OperationStatus.Running, OperationStatus.Failed, OperationStatus.Cancelled]
        };

        public static bool CanTransition(OperationStatus from, OperationStatus to)
        {
            if (from == to) return false;
            return Allowed.TryGetValue(from, out OperationStatus[]? next) && next.Contains(to);
        }

        public static OperationStatus ResolveOutcome(int? exitCode, string successCondition, bool cancelled, bool timedOut, bool verified)
        {
            if (cancelled) return OperationStatus.Cancelled;
            if (timedOut) return OperationStatus.TimedOut;

            bool success = successCondition switch
            {
                "exit-code-zero" => exitCode == 0,
                "exit-code-nonzero-ok" => true,
                "verified" => verified,
                "always" => true,
                _ => false
            };

            if (success && verified) return OperationStatus.Completed;
            if (success && successCondition is "exit-code-zero" or "always") return OperationStatus.Completed;
            return OperationStatus.Failed;
        }

        public static bool IsTerminal(OperationStatus status) => status is
            OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled
            or OperationStatus.TimedOut;
    }

    /// <summary>Server side permission evaluation including privilege escalation protection.</summary>
    public static class PermissionRules
    {
        public static bool HasPermission(IEnumerable<string> permissions, string required)
            => permissions.Contains(required, StringComparer.OrdinalIgnoreCase);

        public static bool HasAnyPermission(IEnumerable<string> permissions, params string[] required)
            => required.Any(r => permissions.Contains(r, StringComparer.OrdinalIgnoreCase));

        /// <summary>A role manager may only grant permissions that they themselves hold.</summary>
        public static bool CanAssignRole(string actorRole, IEnumerable<string> actorPermissions, IEnumerable<string> targetPermissions)
        {
            if (actorRole == RoleNames.SuperAdmin) return true;
            var actorSet = new HashSet<string>(actorPermissions, StringComparer.OrdinalIgnoreCase);
            return targetPermissions.All(actorSet.Contains);
        }

        /// <summary>Protects the last Super Admin account and blocks privilege escalation: only a
        /// Super Admin may modify an account that holds the Super Admin role.</summary>
        public static bool CanModifyUser(string actorRole, IReadOnlyCollection<string> targetRoleNames, AccountStatus targetStatus, int activeSuperAdminCount, string targetUserId, string actorUserId)
        {
            bool targetIsSuperAdmin = targetRoleNames.Contains(RoleNames.SuperAdmin);
            if (targetIsSuperAdmin && activeSuperAdminCount <= 1) return false;
            if (targetIsSuperAdmin && actorRole != RoleNames.SuperAdmin) return false;
            return actorRole == RoleNames.SuperAdmin || actorUserId != targetUserId;
        }
    }

    /// <summary>Input validation rules shared by API and UI.</summary>
    public static class ValidationRules
    {
        public static readonly string[] ReservedUserNames = ["admin", "administrator", "root", "system", "b_vip"];

        /// <summary>
        /// The one e-mail shape this product accepts, on the client and on the server. <see cref="System.Net.Mail.MailAddress"/>
        /// alone is too permissive - it accepts <c>name@host</c> without a top level domain - so the
        /// pattern is applied instead: local part, dot separated domain labels and a 2+ letter suffix.
        /// </summary>
        public static readonly Regex EmailPattern = new(
            @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Longest address RFC 5321 allows.</summary>
        public const int MaximumEmailLength = 254;

        public static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;

            string trimmed = email.Trim();
            return trimmed.Length <= MaximumEmailLength && EmailPattern.IsMatch(trimmed);
        }

        public static bool IsValidUserName(string? userName)
        {
            if (string.IsNullOrWhiteSpace(userName) || userName.Length is < 3 or > 64) return false;
            return userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');
        }

        public static (bool IsValid, string? Error) ValidatePasswordStrength(string? password)
        {
            if (string.IsNullOrEmpty(password)) return (false, "password-required");
            if (password.Length < 10) return (false, "password-too-short");
            if (password.Length > 128) return (false, "password-too-long");
            bool hasLetter = password.Any(char.IsLetter);
            bool hasDigit = password.Any(char.IsDigit);
            bool hasSymbol = password.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            if (!hasLetter || !hasDigit || !hasSymbol) return (false, "password-too-simple");
            return (true, null);
        }

        public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

        public static string NormalizeUserName(string userName) => userName.Trim().ToUpperInvariant();
    }
}
