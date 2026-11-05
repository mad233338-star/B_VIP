using System.Security.Cryptography;

namespace B_VIP.Core.Security
{
    /// <summary>
    /// Password hashing. Two formats are accepted:
    /// <list type="bullet">
    /// <item>PBKDF2$SHA256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt; for accounts created by the API, and</item>
    /// <item>BCrypt ($2a$ / $2b$ / $2y$) for the seeded master administrator.</item>
    /// </list>
    /// Passwords are never stored or logged in clear text, and the format is detected from the
    /// stored value so neither kind of account needs migrating.
    /// </summary>
    public static class PasswordHasher
    {
        public const string Algorithm = "SHA256";
        public const int Iterations = 210_000;
        public const int SaltSize = 16;
        public const int KeySize = 32;

        /// <summary>Work factor for the BCrypt hashes (4..31; 12 ≈ 250 ms on a desktop CPU).</summary>
        public const int BcryptWorkFactor = 12;

        public static string Hash(string password)
        {
            ArgumentNullException.ThrowIfNull(password);

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
            return $"PBKDF2${Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        }

        /// <summary>BCrypt hash, used where the deployment requires that format explicitly.</summary>
        public static string HashBcrypt(string password)
        {
            ArgumentNullException.ThrowIfNull(password);
            return BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);
        }

        /// <summary>True when the stored value is a BCrypt hash rather than the PBKDF2 format.</summary>
        public static bool IsBcrypt(string? stored)
            => stored is not null &&
               (stored.StartsWith("$2a$", StringComparison.Ordinal) ||
                stored.StartsWith("$2b$", StringComparison.Ordinal) ||
                stored.StartsWith("$2y$", StringComparison.Ordinal));

        public static bool Verify(string password, string stored)
        {
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(stored))
            {
                return false;
            }

            if (IsBcrypt(stored))
            {
                try
                {
                    return BCrypt.Net.BCrypt.Verify(password, stored);
                }
                catch (Exception ex) when (ex is BCrypt.Net.SaltParseException
                    or ArgumentException or FormatException or IndexOutOfRangeException)
                {
                    // A truncated or hand-edited hash must read as "wrong password", not as a crash
                    // in the sign-in path.
                    return false;
                }
            }

            string[] parts = stored.Split('$');
            if (parts.Length != 5 || parts[0] != "PBKDF2")
            {
                return false;
            }

            if (!int.TryParse(parts[2], out int iterations) || iterations <= 0)
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(parts[3]);
                byte[] expected = Convert.FromBase64String(parts[4]);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    /// <summary>Opaque token generation and hashing for sessions, refresh tokens and reset links.</summary>
    public static class TokenFactory
    {
        public static string CreateSecureToken(int bytes = 32)
            => Base64Url(RandomNumberGenerator.GetBytes(bytes));

        public static string HashToken(string token)
        {
            ArgumentNullException.ThrowIfNull(token);
            byte[] data = System.Text.Encoding.UTF8.GetBytes(token);
            byte[] digest = SHA256.HashData(data);
            return Convert.ToBase64String(digest);
        }

        public static bool FixedTimeEquals(string? a, string? b)
        {
            if (a is null || b is null) return false;
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(a),
                System.Text.Encoding.UTF8.GetBytes(b));
        }

        public static string Base64Url(ReadOnlySpan<byte> data)
            => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string input)
        {
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }

    public sealed record SignedTokenPayload(
        IReadOnlyDictionary<string, string> Claims,
        DateTime IssuedUtc,
        DateTime ExpiresUtc);

    /// <summary>
    /// Compact HMAC-SHA256 signed token (header.payload.signature) used for access tokens.
    /// The signing key is kept server side (secret store), never inside the desktop executable.
    /// </summary>
    public static class SignedToken
    {
        public const string DefaultIssuer = "B_VIP.Backend";
        public const string DefaultAudience = "B_VIP.Clients";

        public static string Create(
            IReadOnlyDictionary<string, string> claims,
            byte[] signingKey,
            TimeSpan lifetime,
            string issuer = DefaultIssuer,
            string? audience = null,
            DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(claims);
            ArgumentNullException.ThrowIfNull(signingKey);
            if (signingKey.Length < 32) throw new ArgumentException("Signing key must be at least 32 bytes.", nameof(signingKey));

            DateTime now = nowUtc ?? DateTime.UtcNow;
            DateTime expires = now + lifetime;

            var header = new Dictionary<string, string>
            {
                ["alg"] = "HS256",
                ["typ"] = "JWT"
            };

            var payload = new Dictionary<string, string>(claims)
            {
                ["iss"] = issuer,
                ["aud"] = audience ?? DefaultAudience,
                ["iat"] = EpochSeconds(now).ToString(),
                ["exp"] = EpochSeconds(expires).ToString(),
                ["jti"] = TokenFactory.CreateSecureToken(12)
            };

            string headerPart = TokenFactory.Base64Url(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(header)));
            string payloadPart = TokenFactory.Base64Url(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload)));
            string signingInput = $"{headerPart}.{payloadPart}";

            byte[] signature = HMACSHA256.HashData(signingKey, System.Text.Encoding.UTF8.GetBytes(signingInput));
            return $"{signingInput}.{TokenFactory.Base64Url(signature)}";
        }

        public static SignedTokenPayload? Validate(
            string token,
            byte[] signingKey,
            string issuer = DefaultIssuer,
            string? audience = null,
            DateTime? nowUtc = null,
            bool requireExpiry = true)
        {
            if (string.IsNullOrWhiteSpace(token) || signingKey is null || signingKey.Length < 32)
            {
                return null;
            }

            string[] parts = token.Split('.');
            if (parts.Length != 3) return null;

            string signingInput = $"{parts[0]}.{parts[1]}";
            byte[] expected = HMACSHA256.HashData(signingKey, System.Text.Encoding.UTF8.GetBytes(signingInput));
            byte[] actual;
            try
            {
                actual = TokenFactory.FromBase64Url(parts[2]);
            }
            catch (FormatException)
            {
                return null;
            }

            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

            Dictionary<string, string>? payload;
            try
            {
                payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    System.Text.Encoding.UTF8.GetString(TokenFactory.FromBase64Url(parts[1])));
            }
            catch (Exception)
            {
                return null;
            }

            if (payload is null) return null;

            if (payload.TryGetValue("iss", out string? iss) && iss != issuer) return null;
            if (audience is not null && payload.TryGetValue("aud", out string? aud) && aud != audience) return null;

            DateTime now = nowUtc ?? DateTime.UtcNow;
            if (payload.TryGetValue("exp", out string? expValue))
            {
                if (!long.TryParse(expValue, out long exp)) return null;
                if (FromEpoch(exp) <= now) return null;
            }
            else if (requireExpiry)
            {
                return null;
            }

            if (payload.TryGetValue("iat", out string? iatValue) && long.TryParse(iatValue, out long iat))
            {
                if (FromEpoch(iat) > now.AddMinutes(5)) return null;
            }

            var claims = payload
                .Where(kv => kv.Key is not ("iss" or "aud" or "iat" or "exp" or "jti"))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            return new SignedTokenPayload(claims, payload.TryGetValue("iat", out string? i2) && long.TryParse(i2, out long iu) ? FromEpoch(iu) : now, payload.TryGetValue("exp", out string? e2) && long.TryParse(e2, out long eu) ? FromEpoch(eu) : now);
        }

        public static long EpochSeconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        public static DateTime FromEpoch(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }
}
