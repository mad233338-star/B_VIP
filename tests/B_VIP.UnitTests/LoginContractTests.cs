using System.Net;
using System.Net.Http;
using System.Text;
using B_VIP.CloudClient;
using B_VIP.Contracts.Auth;
using Xunit;

namespace B_VIP.UnitTests
{
    /// <summary>
    /// The sign-in contract: a 200 response is only accepted as authentication when it carries a
    /// usable token and an identified account. A deployment that answers every request with a
    /// canned "success" payload (as the legacy demo service does) must fail here, not navigate the
    /// operator into the admin console.
    /// </summary>
    public sealed class LoginContractTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _json;

            public StubHandler(string json) => _json = json;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json")
                });
        }

        private static BVipApiClient Client(string json)
        {
            HttpClient http = new(new StubHandler(json)) { BaseAddress = new Uri("https://example.test/") };
            return new BVipApiClient(http);
        }

        /// <summary>Shape returned by the external demo service: success, a bare token, an admin user.</summary>
        private const string CannedPayload = """
            {
              "success": true,
              "message": "Login successful",
              "token": "not-a-session",
              "user": { "id": "c7b3a1d9-4e2f-4a8b-9c0d-1e2f3a4b5c6d", "username": "Admin", "roles": ["Admin", "Technician"] }
            }
            """;

        private const string RealPayload = """
            {
              "tokens": {
                "accessToken": "header.payload.signature",
                "refreshToken": "refresh-value",
                "expiresUtc": "2026-11-05T10:00:00Z",
                "refreshExpiresUtc": "2026-11-19T10:00:00Z"
              },
              "user": {
                "id": "0f8fad5b-d9cb-469f-a165-70867728950e",
                "userName": "tech1",
                "displayName": "tech1",
                "email": "tech1@b-vip.test",
                "status": "Active",
                "roles": ["User"],
                "createdUtc": "2026-10-01T09:00:00Z",
                "emailVerified": true
              },
              "permissions": [ "operations.run" ],
              "license": null,
              "serverTimeUtc": "2026-11-05T09:00:00Z"
            }
            """;

        [Fact]
        public async Task A_canned_success_payload_without_a_real_session_is_rejected()
        {
            BVipApiClient api = Client(CannedPayload);

            ApiException error = await Assert.ThrowsAsync<ApiException>(
                () => api.LoginAsync(new LoginRequest("random.nobody@example.com", "wrong-password")));

            Assert.Equal("invalid-credentials", error.Code);
            Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
            Assert.False(api.IsAuthenticated);
        }

        /// <summary>
        /// The exact shape the deployed demo endpoint answers with, captured from a real request with
        /// invented credentials: HTTP 200, "Login successful", a bearer token, and the same Admin
        /// account no matter what was typed. The missing token triple alone rejects it today, and the
        /// identity check in the next test covers a canned answer that does include tokens.
        /// </summary>
        private const string DeployedDemoPayload = """
            {
              "success": true,
              "message": "Login successful",
              "token": "header.payload.signature",
              "accessToken": "header.payload.signature",
              "refreshToken": "refresh-value",
              "userId": "c7b3a1d9-4e2f-4a8b-9c0d-1e2f3a4b5c6d",
              "user": {
                "id": "c7b3a1d9-4e2f-4a8b-9c0d-1e2f3a4b5c6d",
                "username": "Admin",
                "email": "Admin@b-vip.com",
                "role": "Admin",
                "roles": ["Admin", "User", "Technician"]
              },
              "roles": ["Admin", "User", "Technician"],
              "isActive": true,
              "expiresAt": "2027-11-05T01:48:54.701Z"
            }
            """;

        [Fact]
        public async Task The_deployed_demo_payload_does_not_sign_an_arbitrary_identifier_in()
        {
            BVipApiClient api = Client(DeployedDemoPayload);

            ApiException error = await Assert.ThrowsAsync<ApiException>(
                () => api.LoginAsync(new LoginRequest("zzz.random", "definitely-wrong")));

            Assert.Equal("invalid-credentials", error.Code);
            Assert.False(api.IsAuthenticated);
        }

        /// <summary>A blanket answer that does carry tokens still must not sign another account in.</summary>
        private const string CannedSessionWithTokens = """
            {
              "tokens": {
                "accessToken": "header.payload.signature",
                "refreshToken": "refresh-value",
                "expiresUtc": "2026-11-05T10:00:00Z",
                "refreshExpiresUtc": "2026-11-19T10:00:00Z"
              },
              "user": {
                "id": "c7b3a1d9-4e2f-4a8b-9c0d-1e2f3a4b5c6d",
                "userName": "Admin",
                "displayName": "Admin",
                "email": "Admin@b-vip.com",
                "status": "Active",
                "roles": ["Admin", "Technician"],
                "createdUtc": "2026-10-01T09:00:00Z"
              },
              "permissions": [ "All" ],
              "license": null,
              "serverTimeUtc": "2026-11-05T09:00:00Z"
            }
            """;

        [Fact]
        public async Task A_session_issued_for_a_different_account_is_rejected()
        {
            BVipApiClient api = Client(CannedSessionWithTokens);

            ApiException error = await Assert.ThrowsAsync<ApiException>(
                () => api.LoginAsync(new LoginRequest("random.nobody@example.com", "wrong-password")));

            Assert.Equal("invalid-credentials", error.Code);
            Assert.False(api.IsAuthenticated);
        }

        [Fact]
        public async Task The_same_session_is_accepted_when_the_identifier_matches_the_account()
        {
            BVipApiClient api = Client(CannedSessionWithTokens);

            SessionResponse session = await api.LoginAsync(new LoginRequest("admin@b-vip.com", "correct-password"));

            Assert.Equal("Admin", session.User.UserName);
            Assert.True(api.IsAuthenticated);
        }

        [Fact]
        public async Task A_real_session_response_is_accepted_and_stores_the_tokens()
        {
            BVipApiClient api = Client(RealPayload);

            SessionResponse session = await api.LoginAsync(new LoginRequest("tech1", "correct-password"));

            Assert.Equal("tech1", session.User.UserName);
            Assert.Contains("User", session.User.Roles);
            Assert.True(api.IsAuthenticated);
            Assert.Equal("header.payload.signature", api.CurrentTokens?.AccessToken);
        }
    }
}
