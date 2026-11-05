using System.Net;
using System.Net.Http.Json;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Licenses;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Reports;
using B_VIP.Contracts.Tickets;
using B_VIP.TestSupport;
using Xunit;

namespace B_VIP.ApiTests
{
    [Collection(ApiCollection.Name)]
    public sealed class OperationFlowTests
    {
        private readonly ApiTestHost _host;

        public OperationFlowTests(ApiTestHost host) => _host = host;

        [Fact]
        public async Task Running_an_operation_requires_an_active_license()
        {
            (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(session);

            HttpResponseMessage response = await client.PostAsJsonAsync("/api/device-operations",
                new { operationType = "adb.devices" });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            ApiError error = await response.ReadErrorAsync();
            Assert.Equal("license-required", error.Code);
        }

        [Fact]
        public async Task Unknown_operation_type_is_rejected_before_any_license_check()
        {
            (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(session);

            HttpResponseMessage response = await client.PostAsJsonAsync("/api/device-operations",
                new { operationType = "does.not.exist" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("unknown-operation", (await response.ReadErrorAsync()).Code);
        }

        [Fact]
        public async Task Data_destructive_operations_need_an_explicit_confirmation()
        {
            (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
            await _host.CreateLicenseAsync(user.Id);
            HttpClient client = _host.CreateClient(session);

            HttpResponseMessage unconfirmed = await client.PostAsJsonAsync("/api/device-operations",
                new { operationType = "fastboot.flash" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unconfirmed.StatusCode);
            Assert.Equal("confirmation-required", (await unconfirmed.ReadErrorAsync()).Code);

            HttpResponseMessage confirmed = await client.PostAsJsonAsync("/api/device-operations",
                new { operationType = "fastboot.flash", confirmDataLoss = true });
            Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        }

        [Fact]
        public async Task Operation_lifecycle_create_get_events_cancel()
        {
            (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
            await _host.CreateLicenseAsync(user.Id);
            HttpClient client = _host.CreateClient(session);

            HttpResponseMessage created = await client.PostAsJsonAsync("/api/device-operations",
                new { operationType = "adb.devices" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            OperationCreatedResponse body = await created.ReadJsonRequiredAsync<OperationCreatedResponse>();
            Assert.Equal("Validating", body.Status);
            Assert.False(string.IsNullOrWhiteSpace(body.CorrelationId));

            HttpResponseMessage get = await client.GetAsync($"/api/device-operations/{body.Id}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            OperationDto operation = await get.ReadJsonRequiredAsync<OperationDto>();
            Assert.Equal("adb.devices", operation.OperationType);
            Assert.Equal(user.Id, operation.UserId);

            HttpResponseMessage events = await client.GetAsync($"/api/device-operations/{body.Id}/events");
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            OperationEventDto[] eventList = await events.ReadJsonRequiredAsync<OperationEventDto[]>();
            Assert.NotEmpty(eventList);
            Assert.Contains(eventList, e => e.Message.Contains("queued", StringComparison.OrdinalIgnoreCase));

            HttpResponseMessage cancel = await client.PostAsync($"/api/device-operations/{body.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            CancelOperationResponse cancelled = await cancel.ReadJsonRequiredAsync<CancelOperationResponse>();
            Assert.True(cancelled.Cancelled);
            Assert.Equal("Cancelled", cancelled.Status);

            // Cancelling a finished operation is rejected without changing anything.
            HttpResponseMessage again = await client.PostAsync($"/api/device-operations/{body.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            CancelOperationResponse second = await again.ReadJsonRequiredAsync<CancelOperationResponse>();
            Assert.False(second.Cancelled);
        }

        [Fact]
        public async Task A_finished_operation_records_its_real_result()
        {
            (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
            await _host.CreateLicenseAsync(user.Id);
            HttpClient client = _host.CreateClient(session);

            HttpResponseMessage created = await client.PostAsJsonAsync("/api/device-operations",
                new { operationType = "adb.devices" });
            OperationCreatedResponse body = await created.ReadJsonRequiredAsync<OperationCreatedResponse>();

            HttpResponseMessage recorded = await client.PostAsJsonAsync($"/api/device-operations/{body.Id}/result",
                new
                {
                    exitCode = 0,
                    timedOut = false,
                    cancelled = false,
                    output = "List of devices attached\nemulator-5554\tdevice",
                    errorOutput = "",
                    summary = "One device detected.",
                    verified = true
                });
            Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);

            OperationDto result = await recorded.ReadJsonRequiredAsync<OperationDto>();
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("Completed", result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        }
    }

    [Collection(ApiCollection.Name)]
    public sealed class LicenseAndDirectoryTests
    {
        private readonly ApiTestHost _host;

        public LicenseAndDirectoryTests(ApiTestHost host) => _host = host;

        [Fact]
        public async Task Admin_creates_reads_and_lists_licenses()
        {
            TestUser user = await _host.CreateUserAsync();
            HttpClient admin = await _host.CreateAdminClientAsync();

            HttpResponseMessage created = await admin.PostAsJsonAsync("/api/licenses",
                new { userId = user.Id, planCode = "trial" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            LicenseDto license = await created.ReadJsonRequiredAsync<LicenseDto>();
            Assert.Equal("trial", license.PlanCode);
            Assert.Equal("Active", license.Status);
            Assert.Equal(user.Id, license.UserId);
            Assert.False(string.IsNullOrWhiteSpace(license.LicenseKey));

            HttpResponseMessage get = await admin.GetAsync($"/api/licenses/{license.Id}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);

            HttpResponseMessage list = await admin.GetAsync($"/api/licenses?userId={user.Id}");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            PagedResponse<LicenseDto> page = await list.ReadJsonRequiredAsync<PagedResponse<LicenseDto>>();
            Assert.Contains(page.Items, l => l.Id == license.Id);
        }

        [Fact]
        public async Task Users_only_see_their_own_licenses()
        {
            TestUser owner = await _host.CreateUserAsync();
            HttpClient admin = await _host.CreateAdminClientAsync();
            HttpResponseMessage created = await admin.PostAsJsonAsync("/api/licenses",
                new { userId = owner.Id, planCode = "trial" });
            LicenseDto ownerLicense = await created.ReadJsonRequiredAsync<LicenseDto>();

            (TestUser stranger, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(session);

            HttpResponseMessage direct = await client.GetAsync($"/api/licenses/{ownerLicense.Id}");
            Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);

            HttpResponseMessage filtered = await client.GetAsync($"/api/licenses?userId={owner.Id}");
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            PagedResponse<LicenseDto> page = await filtered.ReadJsonRequiredAsync<PagedResponse<LicenseDto>>();
            Assert.DoesNotContain(page.Items, l => l.UserId == owner.Id);
            Assert.All(page.Items, l => Assert.Equal(stranger.Id, l.UserId));
        }

        [Fact]
        public async Task User_directory_requires_the_users_read_permission()
        {
            (TestUser plain, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(session);

            HttpResponseMessage denied = await client.GetAsync("/api/users");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            ApiError error = await denied.ReadErrorAsync();
            Assert.Equal("forbidden", error.Code);
            Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));

            HttpClient admin = await _host.CreateAdminClientAsync();
            HttpResponseMessage allowed = await admin.GetAsync("/api/users");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            PagedResponse<UserSummaryDto> page = await allowed.ReadJsonRequiredAsync<PagedResponse<UserSummaryDto>>();
            Assert.Contains(page.Items, u => u.Id == plain.Id);
        }

        [Fact]
        public async Task Admin_dashboard_is_restricted_to_administrators()
        {
            (TestUser plain, SessionResponse session) = await _host.CreateUserSessionAsync();

            HttpResponseMessage denied = await _host.CreateClient(session).GetAsync("/api/admin/overview");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            HttpClient admin = await _host.CreateAdminClientAsync();
            HttpResponseMessage allowed = await admin.GetAsync("/api/admin/overview");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Collection(ApiCollection.Name)]
    public sealed class TicketAndReportTests
    {
        private readonly ApiTestHost _host;

        public TicketAndReportTests(ApiTestHost host) => _host = host;

        [Fact]
        public async Task Ticket_lifecycle_and_owner_only_visibility()
        {
            (TestUser owner, SessionResponse ownerSession) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(ownerSession);

            HttpResponseMessage created = await client.PostAsJsonAsync("/api/tickets",
                new { title = "Phone does not charge", description = "The device stops charging at 40%.", priority = "High" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            TicketDto ticket = await created.ReadJsonRequiredAsync<TicketDto>();
            Assert.StartsWith("TCK-", ticket.Number, StringComparison.Ordinal);
            Assert.Equal("High", ticket.Priority);
            Assert.Equal("Open", ticket.Status);
            Assert.Equal(owner.Id, ticket.UserId);

            HttpResponseMessage ownGet = await client.GetAsync($"/api/tickets/{ticket.Id}");
            Assert.Equal(HttpStatusCode.OK, ownGet.StatusCode);

            HttpResponseMessage message = await client.PostAsJsonAsync($"/api/tickets/{ticket.Id}/messages",
                new { body = "Customer will bring the charger." });
            Assert.Equal(HttpStatusCode.Created, message.StatusCode);

            HttpResponseMessage list = await client.GetAsync("/api/tickets");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            TicketListResponse page = await list.ReadJsonRequiredAsync<TicketListResponse>();
            Assert.Contains(page.Items, t => t.Id == ticket.Id);

            (TestUser stranger, SessionResponse strangerSession) = await _host.CreateUserSessionAsync();
            HttpClient other = _host.CreateClient(strangerSession);

            HttpResponseMessage foreignGet = await other.GetAsync($"/api/tickets/{ticket.Id}");
            Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);

            HttpResponseMessage foreignList = await other.GetAsync("/api/tickets");
            Assert.Equal(HttpStatusCode.OK, foreignList.StatusCode);
            TicketListResponse otherPage = await foreignList.ReadJsonRequiredAsync<TicketListResponse>();
            Assert.DoesNotContain(otherPage.Items, t => t.Id == ticket.Id);
        }

        [Fact]
        public async Task Diagnostic_report_is_created_and_visible_only_to_its_owner()
        {
            (TestUser owner, SessionResponse ownerSession) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(ownerSession);

            HttpResponseMessage created = await client.PostAsJsonAsync("/api/diagnostic-reports",
                new
                {
                    title = "Samsung A series diagnostic",
                    module = "android-diagnostics",
                    deviceId = "R58M12345",
                    contentJson = "{\"battery\":87,\"health\":\"good\"}",
                    containsSensitiveData = false
                });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            DiagnosticReportDto report = await created.ReadJsonRequiredAsync<DiagnosticReportDto>();
            Assert.Equal(owner.Id, report.UserId);

            HttpResponseMessage ownGet = await client.GetAsync($"/api/diagnostic-reports/{report.Id}");
            Assert.Equal(HttpStatusCode.OK, ownGet.StatusCode);

            (TestUser stranger, SessionResponse strangerSession) = await _host.CreateUserSessionAsync();
            HttpResponseMessage foreignGet = await _host.CreateClient(strangerSession)
                .GetAsync($"/api/diagnostic-reports/{report.Id}");
            Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);

            HttpClient admin = await _host.CreateAdminClientAsync();
            HttpResponseMessage adminGet = await admin.GetAsync($"/api/diagnostic-reports/{report.Id}");
            Assert.Equal(HttpStatusCode.OK, adminGet.StatusCode);
        }
    }
}
