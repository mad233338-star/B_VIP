using System.Net;
using System.Net.Http.Json;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Licenses;
using B_VIP.TestSupport;
using Xunit;

namespace B_VIP.ApiTests
{
    /// <summary>
    /// Technician onboarding and the administration around it: self-registration, e-mail
    /// activation, workstation binding, HWID decoupling and subscription extension.
    /// </summary>
    [Collection(ApiCollection.Name)]
    public sealed class TechnicianOnboardingTests
    {
        private const string Password = "Tech!Passw0rd-2026";

        private readonly ApiTestHost _host;

        public TechnicianOnboardingTests(ApiTestHost host) => _host = host;

        private static string UniqueName() => "tech" + Guid.NewGuid().ToString("N")[..10];

        [Fact]
        public async Task Registration_creates_a_pending_account_that_the_activation_link_verifies()
        {
            await _host.EnsureInitializedAsync();

            string userName = UniqueName();
            string email = $"{userName}@b-vip.test";
            HttpClient anonymous = _host.CreateClient();

            HttpResponseMessage register = await anonymous.PostAsJsonAsync("/api/auth/register", new
            {
                userName,
                email,
                password = Password,
                acceptedTerms = true,
                hardwareId = "HWID-ALPHA-0001"
            });

            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            RegisterTechnicianResponse created = await register.ReadJsonRequiredAsync<RegisterTechnicianResponse>();
            Assert.True(created.Success);
            Assert.True(created.RequiresVerification);
            Assert.False(string.IsNullOrWhiteSpace(created.VerificationLink));

            // A pending account cannot sign in yet.
            HttpResponseMessage pendingLogin = await anonymous.PostAsJsonAsync("/api/auth/login",
                new { userName, password = Password });
            Assert.Equal(HttpStatusCode.Forbidden, pendingLogin.StatusCode);
            Assert.Equal("email-not-verified", (await pendingLogin.ReadErrorAsync()).Code);

            // Opening the activation link activates the account.
            Uri link = new(created.VerificationLink!);
            HttpResponseMessage verify = await anonymous.GetAsync(link.PathAndQuery);
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
            Assert.Contains("E-mail verified", await verify.Content.ReadAsStringAsync());

            HttpResponseMessage login = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName,
                password = Password,
                deviceFingerprint = "HWID-ALPHA-0001"
            });

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            SessionResponse session = await login.ReadJsonRequiredAsync<SessionResponse>();
            Assert.True(session.User.EmailVerified);
            Assert.Equal("HWID-ALPHA-0001", session.User.HardwareId);
            Assert.Contains("User", session.User.Roles);
        }

        [Fact]
        public async Task A_technician_cannot_sign_in_from_an_unregistered_workstation()
        {
            (TestUser user, _) = await _host.CreateUserSessionAsync();
            HttpClient anonymous = _host.CreateClient();

            HttpResponseMessage first = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = user.UserName,
                password = user.Password,
                deviceFingerprint = "HWID-FIRST-PC"
            });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            HttpResponseMessage second = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = user.UserName,
                password = user.Password,
                deviceFingerprint = "HWID-SECOND-PC"
            });

            Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
            Assert.Equal("device-mismatch", (await second.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task A_bound_account_is_refused_when_the_client_sends_no_fingerprint()
        {
            (TestUser user, _) = await _host.CreateUserSessionAsync();
            HttpClient anonymous = _host.CreateClient();

            HttpResponseMessage bind = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = user.UserName,
                password = user.Password,
                deviceFingerprint = "HWID-BOUND-PC"
            });
            Assert.Equal(HttpStatusCode.OK, bind.StatusCode);

            // Omitting the fingerprint must not be a way around the binding.
            HttpResponseMessage omitted = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = user.UserName,
                password = user.Password
            });

            Assert.Equal(HttpStatusCode.Forbidden, omitted.StatusCode);
            Assert.Equal("device-mismatch", (await omitted.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task The_master_administrator_signs_in_from_any_workstation()
        {
            await _host.EnsureInitializedAsync();
            HttpClient anonymous = _host.CreateClient();

            HttpResponseMessage first = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = TestEnvironment.AdminUserName,
                password = TestEnvironment.AdminPassword,
                deviceFingerprint = "HWID-ADMIN-CONSOLE-1"
            });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            HttpResponseMessage second = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = TestEnvironment.AdminUserName,
                password = TestEnvironment.AdminPassword,
                deviceFingerprint = "HWID-ADMIN-CONSOLE-2"
            });

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        [Fact]
        public async Task An_administrator_decouples_the_hwid_and_the_new_machine_binds_on_next_sign_in()
        {
            (TestUser user, _) = await _host.CreateUserSessionAsync();
            HttpClient anonymous = _host.CreateClient();

            HttpResponseMessage bind = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = user.UserName,
                password = user.Password,
                deviceFingerprint = "HWID-WORKSHOP-1"
            });
            Assert.Equal(HttpStatusCode.OK, bind.StatusCode);

            HttpClient admin = await _host.CreateAdminClientAsync();
            HttpResponseMessage reset = await admin.PostAsJsonAsync($"/api/admin/technicians/{user.Id}/reset-hwid", new { });
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

            ResetHardwareIdResponse decoupled = await reset.ReadJsonRequiredAsync<ResetHardwareIdResponse>();
            Assert.Null(decoupled.HardwareId);
            Assert.Equal(1, decoupled.HardwareIdResetCount);

            HttpResponseMessage moved = await anonymous.PostAsJsonAsync("/api/auth/login", new
            {
                userName = user.UserName,
                password = user.Password,
                deviceFingerprint = "HWID-WORKSHOP-2"
            });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

            SessionResponse session = await moved.ReadJsonRequiredAsync<SessionResponse>();
            Assert.Equal("HWID-WORKSHOP-2", session.User.HardwareId);
            Assert.Equal(1, session.User.HardwareIdResetCount);

            PagedResponse<UserSummaryDto> directory = await admin
                .GetFromJsonAsync<PagedResponse<UserSummaryDto>>($"/api/admin/technicians?q={Uri.EscapeDataString(user.UserName)}&page=1&pageSize=10")
                ?? throw new InvalidOperationException("The technician directory returned no body.");

            UserSummaryDto row = Assert.Single(directory.Items);
            Assert.Equal("HWID-WORKSHOP-2", row.HardwareId);
            Assert.Equal(1, row.HardwareIdResetCount);
        }

        [Fact]
        public async Task An_administrator_can_verify_an_account_and_extend_its_subscription()
        {
            await _host.EnsureInitializedAsync();
            HttpClient admin = await _host.CreateAdminClientAsync();

            string userName = UniqueName();
            HttpResponseMessage register = await _host.CreateClient().PostAsJsonAsync("/api/auth/register", new
            {
                userName,
                email = $"{userName}@b-vip.test",
                password = Password,
                acceptedTerms = true,
                hardwareId = (string?)null
            });
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);

            PagedResponse<UserSummaryDto> pending = await admin.GetFromJsonAsync<PagedResponse<UserSummaryDto>>(
                $"/api/admin/technicians?q={Uri.EscapeDataString(userName)}&page=1&pageSize=10")
                ?? throw new InvalidOperationException("No directory body.");

            UserSummaryDto technician = Assert.Single(pending.Items);
            Assert.False(technician.EmailVerified);
            Assert.Equal("PendingVerification", technician.Status);

            HttpResponseMessage verify = await admin.PostAsJsonAsync(
                $"/api/admin/technicians/{technician.Id}/verify-email", new { verified = true });
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
            Assert.True((await verify.ReadJsonRequiredAsync<VerifyEmailResponseDto>()).EmailVerified);

            LicenseDto license = await _host.CreateLicenseAsync(technician.Id, "monthly");
            DateTime? before = license.ExpiresAt;

            HttpResponseMessage extend = await admin.PostAsJsonAsync(
                $"/api/admin/technicians/{technician.Id}/extend-subscription", new { days = 30 });
            Assert.Equal(HttpStatusCode.OK, extend.StatusCode);

            ExtendSubscriptionResponse extended = await extend.ReadJsonRequiredAsync<ExtendSubscriptionResponse>();
            Assert.NotNull(extended.ExpiresAt);
            Assert.True(extended.ExpiresAt > before);
            Assert.StartsWith("BVIP-", extended.LicenseKey);

            TechnicianMetricsDto metrics = await admin.GetFromJsonAsync<TechnicianMetricsDto>("/api/admin/technicians/metrics")
                ?? throw new InvalidOperationException("No metrics body.");
            Assert.True(metrics.TotalTechnicians >= 1);
            Assert.True(metrics.ActiveSubscriptions >= 1);
        }
    }
}
