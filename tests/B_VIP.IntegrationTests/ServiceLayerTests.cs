using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Licenses;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Tickets;
using B_VIP.Contracts.Users;
using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace B_VIP.IntegrationTests
{
    /// <summary>
    /// Service-level tests against the real EF Core context and PostgreSQL: licensing gates,
    /// password and session rules, role defaults and owner-scoped queries.
    /// </summary>
    [Collection(IntegrationCollection.Name)]
    public sealed class ServiceLayerTests
    {
        private readonly IntegrationTestHost _host;

        public ServiceLayerTests(IntegrationTestHost host) => _host = host;

        private async Task<IServiceScope> OpenScopeAsync()
        {
            await _host.EnsureInitializedAsync();
            return _host.Services.CreateScope();
        }

        [Fact]
        public async Task Login_issues_tokens_and_refresh_rotates_them()
        {
            using IServiceScope scope = await OpenScopeAsync();
            IAuthService auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            IUserAdminService users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

            string userName = UniqueName();
            await users.CreateUserAsync(
                new CreateUserRequest(userName, $"{userName}@b-vip.test", $"Integration {userName}", TestEnvironment.UserPassword),
                Guid.Empty, null);

            SessionResponse session = await auth.LoginAsync(
                new LoginRequest(userName, TestEnvironment.UserPassword), "127.0.0.1", "xunit", "integration-test");

            Assert.Equal(userName, session.User.UserName);
            Assert.False(string.IsNullOrWhiteSpace(session.Tokens.AccessToken));

            SessionResponse rotated = await auth.RefreshAsync(
                session.Tokens.RefreshToken, "127.0.0.1", "xunit", "integration-test");
            Assert.NotEqual(session.Tokens.RefreshToken, rotated.Tokens.RefreshToken);
            Assert.False(string.IsNullOrWhiteSpace(rotated.Tokens.AccessToken));

            DomainException reuse = await Assert.ThrowsAsync<DomainException>(() =>
                auth.RefreshAsync(session.Tokens.RefreshToken, "127.0.0.1", "xunit", "integration-test"));
            Assert.Equal("invalid-refresh-token", reuse.Code);
            Assert.Equal(401, reuse.StatusCode);
        }

        [Fact]
        public async Task Change_password_rejects_a_wrong_current_password()
        {
            using IServiceScope scope = await OpenScopeAsync();
            IAuthService auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            IUserAdminService users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

            string userName = UniqueName();
            UserSummaryDto created = await users.CreateUserAsync(
                new CreateUserRequest(userName, $"{userName}@b-vip.test", $"Integration {userName}", TestEnvironment.UserPassword),
                Guid.Empty, null);

            DomainException wrong = await Assert.ThrowsAsync<DomainException>(() =>
                auth.ChangePasswordAsync(created.Id, new ChangePasswordRequest("wrong-current", "New!Passw0rd-2026"), null, null));
            Assert.Equal("invalid-password", wrong.Code);

            await auth.ChangePasswordAsync(created.Id,
                new ChangePasswordRequest(TestEnvironment.UserPassword, "New!Passw0rd-2026"), null, null);

            SessionResponse relogin = await auth.LoginAsync(
                new LoginRequest(userName, "New!Passw0rd-2026"), "127.0.0.1", "xunit", "integration-test");
            Assert.Equal(userName, relogin.User.UserName);
        }

        [Fact]
        public async Task Operations_follow_the_license_state_end_to_end()
        {
            using IServiceScope scope = await OpenScopeAsync();
            IAuthService auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            IOperationService operations = scope.ServiceProvider.GetRequiredService<IOperationService>();
            ILicenseService licenses = scope.ServiceProvider.GetRequiredService<ILicenseService>();
            IUserAdminService users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

            string userName = UniqueName();
            UserSummaryDto user = await users.CreateUserAsync(
                new CreateUserRequest(userName, $"{userName}@b-vip.test", $"Integration {userName}", TestEnvironment.UserPassword),
                Guid.Empty, null);

            DomainException withoutLicense = await Assert.ThrowsAsync<DomainException>(() =>
                operations.CreateAsync(new CreateOperationRequest("adb.devices"), user.Id, null, null));
            Assert.Equal("license-required", withoutLicense.Code);
            Assert.Equal(403, withoutLicense.StatusCode);

            LicenseDto license = await licenses.CreateLicenseAsync(
                new CreateLicenseRequest(user.Id, "trial"), user.Id, null);
            Assert.Equal("Active", license.Status);

            OperationCreatedResponse accepted = await operations.CreateAsync(
                new CreateOperationRequest("adb.devices"), user.Id, null, null);
            Assert.Equal("Validating", accepted.Status);

            await licenses.SuspendLicenseAsync(license.Id, "integration suspension", user.Id, null);

            DomainException suspended = await Assert.ThrowsAsync<DomainException>(() =>
                operations.CreateAsync(new CreateOperationRequest("adb.devices"), user.Id, null, null));
            Assert.Equal("license-not-active", suspended.Code);
            Assert.Equal(403, suspended.StatusCode);

            await licenses.ReactivateLicenseAsync(license.Id, user.Id, null);

            OperationCreatedResponse reactivated = await operations.CreateAsync(
                new CreateOperationRequest("adb.devices"), user.Id, null, null);
            Assert.Equal("Validating", reactivated.Status);
        }

        [Fact]
        public async Task Operation_validation_rejects_unknown_and_unconfirmed_requests()
        {
            using IServiceScope scope = await OpenScopeAsync();
            IOperationService operations = scope.ServiceProvider.GetRequiredService<IOperationService>();

            DomainException unknown = await Assert.ThrowsAsync<DomainException>(() =>
                operations.CreateAsync(new CreateOperationRequest("does.not.exist"), Guid.NewGuid(), null, null));
            Assert.Equal("unknown-operation", unknown.Code);
            Assert.Equal(400, unknown.StatusCode);

            DomainException unconfirmed = await Assert.ThrowsAsync<DomainException>(() =>
                operations.CreateAsync(new CreateOperationRequest("fastboot.wipe"), Guid.NewGuid(), null, null));
            Assert.Equal("confirmation-required", unconfirmed.Code);
            Assert.Equal(422, unconfirmed.StatusCode);
        }

        [Fact]
        public async Task New_accounts_get_the_default_role_and_its_permissions()
        {
            using IServiceScope scope = await OpenScopeAsync();
            IUserAdminService users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

            string userName = UniqueName();
            UserSummaryDto created = await users.CreateUserAsync(
                new CreateUserRequest(userName, $"{userName}@b-vip.test", $"Integration {userName}", TestEnvironment.UserPassword),
                Guid.Empty, null);

            UserProfileDto? profile = await users.GetUserAsync(created.Id);
            Assert.NotNull(profile);
            Assert.Contains(RoleNames.User, profile!.Roles);

            IReadOnlyList<string> permissions = await users.GetPermissionsForUserAsync(created.Id);
            Assert.Contains(Permissions.OperationsRun, permissions);
            Assert.Contains(Permissions.TicketsWrite, permissions);
            Assert.DoesNotContain(Permissions.AdminOverview, permissions);
            Assert.DoesNotContain(Permissions.UsersWrite, permissions);
        }

        [Fact]
        public async Task Reference_data_seeding_is_idempotent()
        {
            using IServiceScope scope = await OpenScopeAsync();
            IDataSeeder seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
            IUserAdminService users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

            int before = (await users.GetRolesAsync()).Count;
            await seeder.EnsureSeededAsync();
            await seeder.EnsureSeededAsync();
            int after = (await users.GetRolesAsync()).Count;

            Assert.Equal(RoleNames.All.Length, before);
            Assert.Equal(before, after);
        }

        [Fact]
        public async Task Ticket_queries_stay_within_the_requesting_user()
        {
            using IServiceScope scope = await OpenScopeAsync();
            IUserAdminService users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
            ITicketService tickets = scope.ServiceProvider.GetRequiredService<ITicketService>();

            UserSummaryDto userA = await CreateTestUserAsync(users);
            UserSummaryDto userB = await CreateTestUserAsync(users);

            TicketDto ticketA = await tickets.CreateAsync(
                new CreateTicketRequest("Device A will not boot", "Stuck on the logo.", "Normal"), userA.Id, null, null);
            TicketDto ticketB = await tickets.CreateAsync(
                new CreateTicketRequest("Device B screen flicker", "Flickers at low brightness.", "Normal"), userB.Id, null, null);

            TicketListResponse mine = await tickets.SearchAsync(null, null, userA.Id, true, null, 1, 50);
            Assert.Contains(mine.Items, t => t.Id == ticketA.Id);
            Assert.DoesNotContain(mine.Items, t => t.Id == ticketB.Id);

            Assert.Null(await tickets.GetAsync(ticketB.Id, userA.Id, canSeeAll: false));
            Assert.NotNull(await tickets.GetAsync(ticketA.Id, userA.Id, canSeeAll: false));
            Assert.NotNull(await tickets.GetAsync(ticketB.Id, userB.Id, canSeeAll: true));
        }

        private static string UniqueName() => "i" + Guid.NewGuid().ToString("N")[..12];

        private static Task<UserSummaryDto> CreateTestUserAsync(IUserAdminService users)
        {
            string userName = UniqueName();
            return users.CreateUserAsync(
                new CreateUserRequest(userName, $"{userName}@b-vip.test", $"Integration {userName}", TestEnvironment.UserPassword),
                Guid.Empty, null);
        }
    }
}
