using System.Security.Cryptography;
using System.Text;
using B_VIP.Application.Interfaces;
using B_VIP.Core.Security;
using B_VIP.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace B_VIP.Infrastructure.Security
{
    public interface ISecretProtector
    {
        string Protect(string plaintext);

        string Unprotect(string ciphertext);

        bool IsConfigured { get; }
    }

    /// <summary>AES-256-GCM protection for secrets persisted in the database.</summary>
    public sealed class AesSecretProtector : ISecretProtector
    {
        private readonly byte[]? _key;

        public AesSecretProtector(IOptions<SecurityOptions> options)
        {
            string raw = options.Value.SecretEncryptionKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _key = null;
                return;
            }

            try
            {
                byte[] key = Convert.FromBase64String(raw);
                _key = key.Length == 32 ? key : SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            }
            catch (FormatException)
            {
                _key = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            }
        }

        public bool IsConfigured => _key is not null;

        public string Protect(string plaintext)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            if (_key is null)
            {
                throw new InvalidOperationException(
                    "Security:SecretEncryptionKey is not configured. Set it before storing secrets.");
            }

            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] plain = Encoding.UTF8.GetBytes(plaintext);
            byte[] cipher = new byte[plain.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);

            return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(cipher)}";
        }

        public string Unprotect(string ciphertext)
        {
            if (string.IsNullOrWhiteSpace(ciphertext)) return string.Empty;
            if (_key is null)
            {
                throw new InvalidOperationException("Security:SecretEncryptionKey is not configured.");
            }

            if (!ciphertext.StartsWith("v1.", StringComparison.Ordinal))
            {
                return ciphertext; // already stored in clear by a legacy record: return as-is, never log it
            }

            string[] parts = ciphertext.Split('.');
            if (parts.Length != 4) throw new FormatException("Invalid protected secret format.");

            byte[] nonce = Convert.FromBase64String(parts[1]);
            byte[] tag = Convert.FromBase64String(parts[2]);
            byte[] cipher = Convert.FromBase64String(parts[3]);
            byte[] plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
    }

    /// <summary>Creates and validates the server-signed access tokens used by every protected endpoint.</summary>
    public sealed class HmacTokenProvider : ITokenProvider
    {
        public const string ClaimUser = "sub";
        public const string ClaimSession = "sid";
        public const string ClaimUserName = "usr";
        public const string ClaimEmail = "email";
        public const string ClaimRoles = "rol";
        public const string ClaimPermissions = "per";

        private readonly AuthOptions _options;
        private readonly byte[] _key;

        public HmacTokenProvider(IOptions<AuthOptions> options)
        {
            _options = options.Value;
            if (string.IsNullOrWhiteSpace(_options.SigningKey) || _options.SigningKey.Length < 32)
            {
                throw new InvalidOperationException(
                    "Auth:SigningKey must be configured with at least 32 characters. " +
                    "Provide it through configuration or environment variables, never in source control.");
            }

            _key = Encoding.UTF8.GetBytes(_options.SigningKey);
        }

        public string CreateAccessToken(Guid userId, Guid? sessionId, string userName, IReadOnlyList<string> roles,
            IReadOnlyList<string> permissions, string? email = null)
        {
            var claims = new Dictionary<string, string>
            {
                [ClaimUser] = userId.ToString(),
                [ClaimUserName] = userName,
                [ClaimRoles] = string.Join(',', roles),
                [ClaimPermissions] = string.Join(',', permissions)
            };

            if (!string.IsNullOrWhiteSpace(email))
            {
                claims[ClaimEmail] = email;
            }

            if (sessionId.HasValue)
            {
                claims[ClaimSession] = sessionId.Value.ToString();
            }

            return SignedToken.Create(
                claims,
                _key,
                TimeSpan.FromMinutes(_options.AccessTokenMinutes),
                _options.Issuer,
                _options.Audience);
        }

        public TokenValidationResult? Validate(string accessToken)
        {
            SignedTokenPayload? payload = SignedToken.Validate(accessToken, _key, _options.Issuer, _options.Audience);
            if (payload is null) return null;

            if (!payload.Claims.TryGetValue(ClaimUser, out string? userValue) || !Guid.TryParse(userValue, out Guid userId))
            {
                return null;
            }

            payload.Claims.TryGetValue(ClaimUserName, out string? userName);
            payload.Claims.TryGetValue(ClaimSession, out string? sessionValue);
            Guid? sessionId = Guid.TryParse(sessionValue, out Guid sid) ? sid : null;

            IReadOnlyList<string> Split(string? value)
                => string.IsNullOrWhiteSpace(value)
                    ? Array.Empty<string>()
                    : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            payload.Claims.TryGetValue(ClaimRoles, out string? roles);
            payload.Claims.TryGetValue(ClaimPermissions, out string? permissions);

            return new TokenValidationResult(userId, sessionId, userName ?? string.Empty, Split(roles), Split(permissions));
        }
    }
}
