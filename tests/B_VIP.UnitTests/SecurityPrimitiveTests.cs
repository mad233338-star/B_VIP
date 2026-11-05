using System.Security.Cryptography;
using System.Text;
using B_VIP.Core.Security;
using Xunit;

namespace B_VIP.UnitTests
{
    public sealed class PasswordHasherTests
    {
        [Fact]
        public void Hash_produces_the_documented_pbkdf2_format()
        {
            string stored = PasswordHasher.Hash("Str0ng!Passw0rd");

            string[] parts = stored.Split('$');
            Assert.Equal(5, parts.Length);
            Assert.Equal("PBKDF2", parts[0]);
            Assert.Equal("SHA256", parts[1]);
            Assert.Equal(PasswordHasher.Iterations.ToString(), parts[2]);
            Assert.Equal(PasswordHasher.SaltSize, Convert.FromBase64String(parts[3]).Length);
            Assert.Equal(PasswordHasher.KeySize, Convert.FromBase64String(parts[4]).Length);
        }

        [Fact]
        public void Verify_accepts_the_correct_password_only()
        {
            string stored = PasswordHasher.Hash("Str0ng!Passw0rd");

            Assert.True(PasswordHasher.Verify("Str0ng!Passw0rd", stored));
            Assert.False(PasswordHasher.Verify("wrong!Passw0rd", stored));
            Assert.False(PasswordHasher.Verify("", stored));
            Assert.False(PasswordHasher.Verify("Str0ng!Passw0rd", string.Empty));
        }

        [Fact]
        public void Two_hashes_of_the_same_password_use_different_salts()
        {
            string first = PasswordHasher.Hash("same-password-1!");
            string second = PasswordHasher.Hash("same-password-1!");

            Assert.NotEqual(first, second);
            Assert.True(PasswordHasher.Verify("same-password-1!", first));
            Assert.True(PasswordHasher.Verify("same-password-1!", second));
        }

        [Theory]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("PBKDF2$SHA256$notanumber$c2FsdA==$aGFzaA==")]
        [InlineData("PBKDF2$SHA256$1000$!!!invalid-base64!!!$aGFzaA==")]
        [InlineData("OTHER$SHA256$1000$c2FsdA==$aGFzaA==")]
        public void Verify_rejects_malformed_or_foreign_stored_values(string stored)
        {
            Assert.False(PasswordHasher.Verify("whatever", stored));
        }
    }

    public sealed class TokenFactoryTests
    {
        [Fact]
        public void HashToken_is_deterministic_and_never_returns_the_plaintext()
        {
            string token = TokenFactory.CreateSecureToken();

            string first = TokenFactory.HashToken(token);
            string second = TokenFactory.HashToken(token);

            Assert.Equal(first, second);
            Assert.NotEqual(token, first);
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))), first);
        }

        [Fact]
        public void CreateSecureToken_produces_url_safe_unique_tokens()
        {
            string first = TokenFactory.CreateSecureToken();
            string second = TokenFactory.CreateSecureToken();

            Assert.NotEqual(first, second);
            Assert.DoesNotContain("+", first);
            Assert.DoesNotContain("/", first);
            Assert.DoesNotContain("=", first);
            Assert.Equal(43, first.Length); // 32 bytes, base64url without padding
        }

        [Fact]
        public void Base64Url_roundtrips_arbitrary_bytes()
        {
            byte[] data = RandomNumberGenerator.GetBytes(48);
            string encoded = TokenFactory.Base64Url(data);

            Assert.DoesNotContain("+", encoded);
            Assert.DoesNotContain("/", encoded);
            Assert.DoesNotContain("=", encoded);
            Assert.Equal(data, TokenFactory.FromBase64Url(encoded));
        }

        [Fact]
        public void FromBase64Url_rejects_invalid_characters()
        {
            Assert.Throws<FormatException>(() => TokenFactory.FromBase64Url("~~notbase64~~"));
        }

        [Fact]
        public void FixedTimeEquals_compares_exactly()
        {
            Assert.True(TokenFactory.FixedTimeEquals("abc", "abc"));
            Assert.False(TokenFactory.FixedTimeEquals("abc", "abd"));
            Assert.False(TokenFactory.FixedTimeEquals("abc", "abc "));
            Assert.False(TokenFactory.FixedTimeEquals((string?)null, "abc"));
            Assert.False(TokenFactory.FixedTimeEquals("abc", (string?)null));
        }
    }

    public sealed class SignedTokenTests
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes(new string('k', 48));

        [Fact]
        public void Create_and_validate_roundtrip_the_custom_claims()
        {
            var claims = new Dictionary<string, string>
            {
                ["sub"] = Guid.NewGuid().ToString(),
                ["rol"] = "Super Admin,User"
            };

            string token = SignedToken.Create(claims, Key, TimeSpan.FromMinutes(5));
            SignedTokenPayload? payload = SignedToken.Validate(token, Key);

            Assert.NotNull(payload);
            Assert.Equal(claims["sub"], payload!.Claims["sub"]);
            Assert.Equal("Super Admin,User", payload.Claims["rol"]);
            Assert.DoesNotContain("iss", payload.Claims.Keys);
            Assert.DoesNotContain("exp", payload.Claims.Keys);
            Assert.DoesNotContain("jti", payload.Claims.Keys);
        }

        [Fact]
        public void A_tampered_signature_is_rejected()
        {
            string token = SignedToken.Create(new Dictionary<string, string> { ["sub"] = "1" }, Key, TimeSpan.FromMinutes(5));
            int lastDot = token.LastIndexOf('.');
            string tampered = token[..lastDot] + ".AAAAAAAAAAAAAAAAAAAAAAAAAAAA";

            Assert.Null(SignedToken.Validate(tampered, Key));
        }

        [Fact]
        public void A_tampered_payload_is_rejected()
        {
            string token = SignedToken.Create(new Dictionary<string, string> { ["sub"] = "1" }, Key, TimeSpan.FromMinutes(5));
            string[] parts = token.Split('.');
            string forgedPayload = TokenFactory.Base64Url(Encoding.UTF8.GetBytes(
                "{\"sub\":\"999\",\"iss\":\"B_VIP.Backend\",\"aud\":\"B_VIP.Clients\",\"exp\":9999999999}"));
            string forged = $"{parts[0]}.{forgedPayload}.{parts[2]}";

            Assert.Null(SignedToken.Validate(forged, Key));
        }

        [Fact]
        public void An_expired_token_is_rejected()
        {
            string token = SignedToken.Create(new Dictionary<string, string> { ["sub"] = "1" }, Key,
                TimeSpan.FromMinutes(5), nowUtc: DateTime.UtcNow.AddMinutes(-30));

            Assert.Null(SignedToken.Validate(token, Key));
        }

        [Fact]
        public void A_token_issued_in_the_future_beyond_the_clock_skew_is_rejected()
        {
            string token = SignedToken.Create(new Dictionary<string, string> { ["sub"] = "1" }, Key,
                TimeSpan.FromHours(1), nowUtc: DateTime.UtcNow.AddMinutes(30));

            Assert.Null(SignedToken.Validate(token, Key));
        }

        [Fact]
        public void The_wrong_key_is_rejected()
        {
            string token = SignedToken.Create(new Dictionary<string, string> { ["sub"] = "1" }, Key, TimeSpan.FromMinutes(5));
            byte[] otherKey = Encoding.UTF8.GetBytes(new string('x', 48));

            Assert.Null(SignedToken.Validate(token, otherKey));
        }

        [Fact]
        public void The_wrong_issuer_or_audience_is_rejected()
        {
            string token = SignedToken.Create(new Dictionary<string, string> { ["sub"] = "1" }, Key,
                TimeSpan.FromMinutes(5), issuer: "Someone.Else");

            Assert.Null(SignedToken.Validate(token, Key));

            string goodIssuer = SignedToken.Create(new Dictionary<string, string> { ["sub"] = "1" }, Key,
                TimeSpan.FromMinutes(5), audience: "Other.Clients");
            Assert.Null(SignedToken.Validate(goodIssuer, Key, SignedToken.DefaultIssuer, SignedToken.DefaultAudience));
        }

        [Fact]
        public void A_short_signing_key_is_refused()
        {
            Assert.Throws<ArgumentException>(() =>
                SignedToken.Create(new Dictionary<string, string>(), Encoding.UTF8.GetBytes("short"), TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void A_garbage_token_string_is_rejected_without_throwing()
        {
            Assert.Null(SignedToken.Validate("", Key));
            Assert.Null(SignedToken.Validate("not.a.token", Key));
            Assert.Null(SignedToken.Validate("onepart", Key));
            Assert.Null(SignedToken.Validate("a.b.@@@@", Key));
        }
    }
}
