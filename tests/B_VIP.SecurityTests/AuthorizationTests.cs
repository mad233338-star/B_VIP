using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.TestSupport;
using Xunit;

namespace B_VIP.SecurityTests
{
    /// <summary>Deny-by-default authentication, permission gating and brute-force protection.</summary>
    [Collection(SecurityCollection.Name)]
    public sealed class AuthorizationTests
    {
        private static readonly string[] ProtectedPaths =
        [
            "/api/users",
            "/api/licenses",
            "/api/device-operations",
            "/api/diagnostic-reports",
            "/api/tickets",
            "/api/notifications",
            "/api/admin/overview",
            "/api/admin/audit-events",
            "/api/admin/roles",
            "/api/auth/me"
        ];

        private readonly SecurityTestHost _host;
        private readonly StrictRateLimitHost _strict;

        public AuthorizationTests(SecurityTestHost host, StrictRateLimitHost strict)
        {
            _host = host;
            _strict = strict;
        }

        [Fact]
        public async Task Every_private_endpoint_rejects_anonymous_requests()
        {
            HttpClient client = _host.CreateClient();

            foreach (string path in ProtectedPaths)
            {
                HttpResponseMessage response = await client.GetAsync(path);
                Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"{path} answered {(int)response.StatusCode} without a token");

                if (response.StatusCode is HttpStatusCode.Unauthorized)
                {
                    ApiError error = await response.ReadErrorAsync();
                    Assert.Equal("unauthorized", error.Code);
                    Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId), $"{path} returned no correlation id");
                }
            }
        }

        [Fact]
        public async Task Published_endpoints_stay_reachable_without_a_token()
        {
            HttpClient client = _host.CreateClient();

            foreach (string path in new[] { "/api/meta", "/api/health", "/api/bootstrap/status" })
            {
                HttpResponseMessage response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        [Fact]
        public async Task Malformed_and_tampered_tokens_are_rejected()
        {
            HttpClient client = _host.CreateClient();

            foreach (string token in new[] { "garbage", "a.b.c", "null" })
            {
                HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                HttpResponseMessage response = await client.SendAsync(request);

                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("invalid-token", (await response.ReadErrorAsync()).Code);
            }

            SessionResponse session = await _host.AdminSessionAsync();
            string[] parts = session.Tokens.AccessToken.Split('.');
            Assert.Equal(3, parts.Length);
            string forged = parts[0] + "." + parts[1] + "." + Reverse(parts[2]);

            HttpRequestMessage tampered = new(HttpMethod.Get, "/api/auth/me");
            tampered.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged);
            HttpResponseMessage forgedResponse = await client.SendAsync(tampered);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedResponse.StatusCode);

            HttpRequestMessage wrongScheme = new(HttpMethod.Get, "/api/auth/me");
            wrongScheme.Headers.Authorization = new AuthenticationHeaderValue("Basic", session.Tokens.AccessToken);
            HttpResponseMessage basicResponse = await client.SendAsync(wrongScheme);
            Assert.Equal(HttpStatusCode.Unauthorized, basicResponse.StatusCode);
        }

        [Fact]
        public async Task Missing_permission_returns_a_403_envelope()
        {
            (TestUser plain, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(session);

            foreach (string path in new[] { "/api/users", "/api/admin/overview", "/api/admin/audit-events", "/api/admin/roles" })
            {
                HttpResponseMessage response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

                ApiError error = await response.ReadErrorAsync();
                Assert.Equal("forbidden", error.Code);
                Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId), $"{path} returned no correlation id");
                Assert.Contains("permission", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Repeated_failed_logins_lock_the_account()
        {
            HttpClient client = _host.CreateClient();
            TestUser user = await _host.CreateUserAsync();
            const string wrongPassword = "not-the-password";

            for (int attempt = 0; attempt < 5; attempt++)
            {
                HttpResponseMessage failed = await client.PostAsJsonAsync("/api/auth/login",
                    new { userName = user.UserName, password = wrongPassword });
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
                Assert.Equal("invalid-credentials", (await failed.ReadErrorAsync()).Code);
            }

            HttpResponseMessage locked = await client.PostAsJsonAsync("/api/auth/login",
                new { userName = user.UserName, password = user.Password });
            Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
            Assert.Equal("account-locked", (await locked.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task Login_failure_does_not_reveal_whether_the_account_exists()
        {
            HttpClient client = _host.CreateClient();

            HttpResponseMessage unknownUser = await client.PostAsJsonAsync("/api/auth/login",
                new { userName = "no-such-user-" + Guid.NewGuid().ToString("N")[..8], password = "whatever-1234" });
            Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
            ApiError unknownError = await unknownUser.ReadErrorAsync();

            TestUser existing = await _host.CreateUserAsync();
            HttpResponseMessage wrongPassword = await client.PostAsJsonAsync("/api/auth/login",
                new { userName = existing.UserName, password = "whatever-1234" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
            ApiError existingError = await wrongPassword.ReadErrorAsync();

            Assert.Equal("invalid-credentials", unknownError.Code);
            Assert.Equal(unknownError.Code, existingError.Code);
            Assert.Equal(unknownError.Message, existingError.Message);
        }

        [Fact]
        public async Task Repeated_login_attempts_are_rate_limited()
        {
            HttpClient client = _strict.CreateClient();
            var statuses = new List<HttpStatusCode>();
            string? rateLimitCode = null;

            for (int attempt = 0; attempt < 12; attempt++)
            {
                HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
                    new { userName = $"probe{attempt}-{Guid.NewGuid().ToString("N")[..8]}", password = "wrong-password-1" });
                statuses.Add(response.StatusCode);

                if (response.StatusCode is HttpStatusCode.TooManyRequests && rateLimitCode is null)
                {
                    rateLimitCode = (await response.ReadErrorAsync()).Code;
                }
            }

            Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
            Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
            Assert.All(statuses, s => Assert.True(
                s is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"Unexpected status {(int)s}"));
            Assert.Contains(rateLimitCode, new[] { "rate-limited", "too-many-attempts" });
        }

        private static string Reverse(string value)
        {
            char[] chars = value.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }
    }
}
