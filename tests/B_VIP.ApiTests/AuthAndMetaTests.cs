using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.TestSupport;
using Xunit;

namespace B_VIP.ApiTests
{
    [Collection(ApiCollection.Name)]
    public sealed class AuthAndMetaTests
    {
        private readonly ApiTestHost _host;

        public AuthAndMetaTests(ApiTestHost host) => _host = host;

        [Fact]
        public async Task Login_returns_tokens_user_and_permissions()
        {
            SessionResponse session = await _host.AdminSessionAsync();

            Assert.False(string.IsNullOrWhiteSpace(session.Tokens.AccessToken));
            Assert.False(string.IsNullOrWhiteSpace(session.Tokens.RefreshToken));
            Assert.Equal(TestEnvironment.AdminUserName, session.User.UserName);
            Assert.Contains("Super Admin", session.User.Roles);
            Assert.NotEmpty(session.Permissions);
        }

        [Fact]
        public async Task Login_with_wrong_password_returns_generic_401()
        {
            TestUser user = await _host.CreateUserAsync();
            HttpClient client = _host.CreateClient();

            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
                new { userName = user.UserName, password = "definitely-not-the-password" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            ApiError error = await response.ReadErrorAsync();
            Assert.Equal("invalid-credentials", error.Code);
            Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
        }

        [Fact]
        public async Task Login_without_credentials_returns_400()
        {
            HttpClient client = _host.CreateClient();

            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
                new { userName = "", password = "" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid-request", (await response.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task Refresh_rotates_the_refresh_token_and_rejects_reuse()
        {
            (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient();

            HttpResponseMessage first = await client.PostAsJsonAsync("/api/auth/refresh",
                new { refreshToken = session.Tokens.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            SessionResponse rotated = await first.ReadJsonRequiredAsync<SessionResponse>();
            Assert.False(string.IsNullOrWhiteSpace(rotated.Tokens.AccessToken));
            Assert.NotEqual(session.Tokens.RefreshToken, rotated.Tokens.RefreshToken);

            HttpResponseMessage reuse = await client.PostAsJsonAsync("/api/auth/refresh",
                new { refreshToken = session.Tokens.RefreshToken });
            Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
            Assert.Equal("invalid-refresh-token", (await reuse.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task Refresh_with_a_garbage_token_is_rejected()
        {
            HttpClient client = _host.CreateClient();

            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/refresh",
                new { refreshToken = "not-a-refresh-token" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid-refresh-token", (await response.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task Me_requires_a_token_and_returns_the_current_user()
        {
            HttpClient client = _host.CreateClient();

            HttpResponseMessage anonymous = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            TestUser user = await _host.CreateUserAsync();
            SessionResponse session = await _host.GetSessionAsync(user.UserName, user.Password);

            HttpResponseMessage authed = await _host.CreateClient(session).GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, authed.StatusCode);

            MeResponse me = await authed.ReadJsonRequiredAsync<MeResponse>();
            Assert.Equal(user.UserName, me.User.UserName);
        }

        [Fact]
        public async Task Malformed_bearer_token_is_rejected()
        {
            HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

            HttpResponseMessage response = await _host.CreateClient().SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid-token", (await response.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task Change_password_validates_the_current_password()
        {
            TestUser user = await _host.CreateUserAsync();
            SessionResponse session = await _host.GetSessionAsync(user.UserName, user.Password);
            HttpClient client = _host.CreateClient(session);
            const string newPassword = "New!Passw0rd-2026";

            HttpResponseMessage wrong = await client.PostAsJsonAsync("/api/auth/change-password",
                new { currentPassword = "wrong-password", newPassword });
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("invalid-password", (await wrong.ReadErrorAsync()).Code);

            HttpResponseMessage ok = await client.PostAsJsonAsync("/api/auth/change-password",
                new { currentPassword = user.Password, newPassword });
            Assert.True(ok.IsSuccessStatusCode, $"Changing the password failed: {(int)ok.StatusCode}");

            HttpResponseMessage relogin = await _host.CreateClient().PostAsJsonAsync("/api/auth/login",
                new { userName = user.UserName, password = newPassword });
            Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        }

        [Fact]
        public async Task Meta_is_reachable_without_a_token_and_describes_the_product()
        {
            HttpResponseMessage response = await _host.CreateClient().GetAsync("/api/meta");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("B_VIP Tool", body, StringComparison.Ordinal);
            Assert.Contains("Bashir Al-Farhan", body, StringComparison.Ordinal);
            // English-only product: the language list advertises English and nothing else.
            Assert.Contains("\"code\":\"en\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"code\":\"ar\"", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Health_check_is_reachable_without_a_token()
        {
            HttpResponseMessage response = await _host.CreateClient().GetAsync("/api/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Bootstrap_status_is_public_and_reports_the_seeded_admin()
        {
            HttpResponseMessage response = await _host.CreateClient().GetAsync("/api/bootstrap/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"requiresFirstAdmin\":false", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
