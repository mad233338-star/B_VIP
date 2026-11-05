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

namespace B_VIP.SecurityTests
{
    /// <summary>Cross-account data isolation, hostile input and bootstrap replay protection.</summary>
    [Collection(SecurityCollection.Name)]
    public sealed class IsolationTests
    {
        private readonly SecurityTestHost _host;

        public IsolationTests(SecurityTestHost host) => _host = host;

        /// <summary>A signed-in owner holding one record of every kind.</summary>
        private sealed record OwnerRecords(
            TestUser User,
            HttpClient Client,
            LicenseDto License,
            OperationCreatedResponse Operation,
            TicketDto Ticket,
            Guid ReportId);

        private async Task<OwnerRecords> CreateOwnerAsync()
        {
            (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(session);

            LicenseDto license = await _host.CreateLicenseAsync(user.Id);

            HttpResponseMessage operation = await client.PostAsJsonAsync("/api/device-operations",
                new { operationType = "adb.devices" });
            Assert.Equal(HttpStatusCode.Created, operation.StatusCode);
            OperationCreatedResponse op = await operation.ReadJsonRequiredAsync<OperationCreatedResponse>();

            HttpResponseMessage ticket = await client.PostAsJsonAsync("/api/tickets",
                new { title = "Private support request", description = "Customer data inside.", priority = "Normal" });
            Assert.Equal(HttpStatusCode.Created, ticket.StatusCode);
            TicketDto createdTicket = await ticket.ReadJsonRequiredAsync<TicketDto>();

            HttpResponseMessage report = await client.PostAsJsonAsync("/api/diagnostic-reports",
                new
                {
                    title = "Private diagnostic report",
                    module = "android-diagnostics",
                    deviceId = "PRIV-DEVICE-1",
                    contentJson = "{\"secret\":\"customer-data\"}",
                    containsSensitiveData = true
                });
            Assert.Equal(HttpStatusCode.Created, report.StatusCode);
            DiagnosticReportDto createdReport = await report.ReadJsonRequiredAsync<DiagnosticReportDto>();

            return new OwnerRecords(user, client, license, op, createdTicket, createdReport.Id);
        }

        [Fact]
        public async Task Another_user_cannot_read_or_touch_foreign_records()
        {
            OwnerRecords owner = await CreateOwnerAsync();
            (TestUser intruder, SessionResponse intruderSession) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(intruderSession);

            HttpResponseMessage operation = await client.GetAsync($"/api/device-operations/{owner.Operation.Id}");
            Assert.Equal(HttpStatusCode.NotFound, operation.StatusCode);

            HttpResponseMessage events = await client.GetAsync($"/api/device-operations/{owner.Operation.Id}/events");
            Assert.Equal(HttpStatusCode.NotFound, events.StatusCode);

            HttpResponseMessage cancel = await client.PostAsync($"/api/device-operations/{owner.Operation.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);

            HttpResponseMessage result = await client.PostAsJsonAsync($"/api/device-operations/{owner.Operation.Id}/result",
                new { exitCode = 0, timedOut = false, cancelled = false, output = "", errorOutput = "", summary = "x", verified = false });
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);

            HttpResponseMessage license = await client.GetAsync($"/api/licenses/{owner.License.Id}");
            Assert.Equal(HttpStatusCode.NotFound, license.StatusCode);

            HttpResponseMessage ticket = await client.GetAsync($"/api/tickets/{owner.Ticket.Id}");
            Assert.Equal(HttpStatusCode.NotFound, ticket.StatusCode);

            HttpResponseMessage report = await client.GetAsync($"/api/diagnostic-reports/{owner.ReportId}");
            Assert.Equal(HttpStatusCode.NotFound, report.StatusCode);

            // The administrator, who holds the support permissions, still sees everything.
            HttpClient admin = await _host.CreateAdminClientAsync();
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/device-operations/{owner.Operation.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/licenses/{owner.License.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/tickets/{owner.Ticket.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/diagnostic-reports/{owner.ReportId}")).StatusCode);
        }

        [Fact]
        public async Task List_endpoints_ignores_foreign_user_filters()
        {
            OwnerRecords owner = await CreateOwnerAsync();
            (TestUser intruder, SessionResponse intruderSession) = await _host.CreateUserSessionAsync();
            HttpClient client = _host.CreateClient(intruderSession);

            HttpResponseMessage operations = await client.GetAsync($"/api/device-operations?userId={owner.User.Id}");
            Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
            OperationListResponse opPage = await operations.ReadJsonRequiredAsync<OperationListResponse>();
            Assert.All(opPage.Items, o => Assert.Equal(intruder.Id, o.UserId));

            HttpResponseMessage licenses = await client.GetAsync($"/api/licenses?userId={owner.User.Id}");
            Assert.Equal(HttpStatusCode.OK, licenses.StatusCode);
            PagedResponse<LicenseDto> licensePage = await licenses.ReadJsonRequiredAsync<PagedResponse<LicenseDto>>();
            Assert.All(licensePage.Items, l => Assert.Equal(intruder.Id, l.UserId));

            HttpResponseMessage tickets = await client.GetAsync("/api/tickets");
            Assert.Equal(HttpStatusCode.OK, tickets.StatusCode);
            TicketListResponse ticketPage = await tickets.ReadJsonRequiredAsync<TicketListResponse>();
            Assert.DoesNotContain(ticketPage.Items, t => t.Id == owner.Ticket.Id);

            HttpResponseMessage reports = await client.GetAsync($"/api/diagnostic-reports?userId={owner.User.Id}");
            Assert.Equal(HttpStatusCode.OK, reports.StatusCode);
            PagedResponse<DiagnosticReportDto> reportPage = await reports.ReadJsonRequiredAsync<PagedResponse<DiagnosticReportDto>>();
            Assert.All(reportPage.Items, r => Assert.Equal(intruder.Id, r.UserId));
        }

        [Fact]
        public async Task Sql_style_filters_are_treated_as_plain_text()
        {
            HttpClient admin = await _host.CreateAdminClientAsync();

            HttpResponseMessage users = await admin.GetAsync("/api/users?q=" + Uri.EscapeDataString("' OR '1'='1"));
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);

            HttpResponseMessage licenses = await admin.GetAsync("/api/licenses?q=" + Uri.EscapeDataString("'; DROP TABLE users;--"));
            Assert.Equal(HttpStatusCode.OK, licenses.StatusCode);

            (TestUser plain, SessionResponse session) = await _host.CreateUserSessionAsync();
            HttpResponseMessage tickets = await _host.CreateClient(session)
                .GetAsync("/api/tickets?q=" + Uri.EscapeDataString("' OR 1=1 --"));
            Assert.Equal(HttpStatusCode.OK, tickets.StatusCode);

            // The database is still alive and answering.
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
        }

        [Fact]
        public async Task Bootstrap_cannot_be_replayed_with_a_wrong_token()
        {
            HttpClient client = _host.CreateClient();

            HttpResponseMessage wrongToken = await client.PostAsJsonAsync("/api/bootstrap/first-admin",
                new { token = "WRONG-TOKEN", userName = "attacker", email = "attacker@b-vip.test", password = "Attacker!Passw0rd1" });
            Assert.False(wrongToken.IsSuccessStatusCode,
                $"A wrong bootstrap token answered {(int)wrongToken.StatusCode}");

            HttpResponseMessage replay = await client.PostAsJsonAsync("/api/bootstrap/first-admin",
                new { token = TestEnvironment.BootstrapToken, userName = "second.root", email = "second@b-vip.test", password = "Second!Passw0rd1" });
            Assert.False(replay.StatusCode is HttpStatusCode.Created,
                "The first-admin bootstrap was replayed after it had already run");

            // The original administrator is still the only Super Admin.
            HttpClient admin = await _host.CreateAdminClientAsync();
            HttpResponseMessage admins = await admin.GetAsync("/api/users?role=" + Uri.EscapeDataString("Super Admin"));
            Assert.Equal(HttpStatusCode.OK, admins.StatusCode);
            string body = await admins.Content.ReadAsStringAsync();
            Assert.DoesNotContain("attacker", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("second.root", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
