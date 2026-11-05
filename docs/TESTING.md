# B_VIP Tool — Testing Guide

218 automated tests across five suites. They run with `dotnet test` and need no external services
except PostgreSQL for the three backend suites.

---

## 1. Suites

| Project | Tests | What it covers | Needs PostgreSQL |
|---|---|---|---|
| `tests/B_VIP.UnitTests` | 135 | Domain rules: operation state machine and outcome resolution, license validity/expiry/features, permission matrix and escalation rules, password hashing, token signing/validation, secret protection, key generation, report redaction, validation rules | no |
| `tests/B_VIP.HardwareTests` | 43 | `adb`/`fastboot` output parsing, device detection with fake processes, diagnostics, module registry and execution, tool location | no |
| `tests/B_VIP.ApiTests` | 22 | Real HTTP against the real host: health/meta, login/refresh/reuse, `/me`, bootstrap, users, licenses, operation lifecycle, reports, tickets, notifications | yes |
| `tests/B_VIP.IntegrationTests` | 7 | Service layer against a real migrated database: seeding, auth service, license issuance/verification, operation gating, audit writes | yes |
| `tests/B_VIP.SecurityTests` | 11 | Anonymous access refusal, permission matrix per role, cross-account isolation, hostile input, bootstrap replay, maintenance mode, rate limiting | yes |

`tests/B_VIP.TestSupport` is the shared harness: `BackendTestHost` boots the real `Program` with
`WebApplicationFactory`, creates a disposable database, applies the real migrations and seed data,
creates the first admin and caches sessions.

## 2. Running

```powershell
dotnet build B_VIP_Software.sln
dotnet test  B_VIP_Software.sln                     # everything
dotnet test  tests/B_VIP.UnitTests                  # one suite
dotnet test  tests/B_VIP.ApiTests --filter "FullyQualifiedName~Ticket"   # one area
dotnet test  B_VIP_Software.sln --logger "trx;LogFileName=results.trx"   # CI artifact
```

No PostgreSQL needed for the two non-backend suites.

## 3. Database used by the tests

`TestEnvironment` (in `tests/B_VIP.TestSupport/BackendTestHost.cs`) points at:

```
Host=127.0.0.1;Port=5433;Username=postgres;Password=
```

Each test host owns its database (`b_vip_api_test`, `b_vip_integration_test`, `b_vip_security_test`,
plus `b_vip_security_strict` for the rate-limit tests). At the start of a run the harness creates the
database when it is missing and then empties the `public` schema, so migrations and seeding always
run from scratch. Nothing outside those databases is touched.

To point the suites at a different server, edit the constants in `TestEnvironment`:

```csharp
public const string Host = "127.0.0.1";
public const int Port = 5433;
public const string PgUserName = "postgres";
public static string ConnectionString(string database)
    => $"Host={Host};Port={Port};Database={database};Username={PgUserName};Password=...;...";
```

For a throw-away local cluster (never a shared one), speed the tests up considerably:

```conf
fsync = off
synchronous_commit = off
full_page_writes = off
```

## 4. What the harness gives you in a test

```csharp
[Collection(ApiCollection.Name)]
public sealed class MyTests
{
    private readonly ApiTestHost _host;
    public MyTests(ApiTestHost host) => _host = host;

    [Fact]
    public async Task A_user_can_read_their_own_operation()
    {
        (TestUser user, SessionResponse session) = await _host.CreateUserSessionAsync();
        await _host.CreateLicenseAsync(user.Id, "trial");
        HttpClient client = _host.CreateClient(session);

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/device-operations",
            new { operationType = "adb.devices" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
```

Available helpers: `CreateClient()`, `CreateClient(session)`, `AdminSessionAsync()`,
`CreateAdminClientAsync()`, `CreateUserAsync(role)`, `CreateUserSessionAsync(role)`,
`CreateLicenseAsync(userId, planCode)`.

## 5. Conventions

- One `[Collection]` per host so xUnit never runs two hosts against the same database in parallel.
- Tests assert HTTP status **and** the error code from the `{"error":{...}}` envelope.
- Hostile input tests send malformed JSON, oversized strings, foreign ids and replayed bootstrap
  tokens; they assert both the status and that no data leaked.
- Do not weaken an assertion to make a test pass: a red test is information, not an obstacle.
- Time-dependent behavior uses short waits (milliseconds), never `Thread.Sleep` loops over minutes.

## 6. Continuous integration

Any CI system that has the .NET 8 SDK and a PostgreSQL service works:

```yaml
services:
  postgres:
    image: postgres:16
    env: { POSTGRES_HOST_AUTH_METHOD: trust }
    ports: [ "5433:5432" ]
steps:
  - run: dotnet build B_VIP_Software.sln -c Release
  - run: dotnet test B_VIP_Software.sln -c Release --logger "trx;LogFileName=results.trx"
```

The WPF projects build only on Windows; the backend and the three backend suites build anywhere.

## 7. Manual smoke test before a release

1. Start the backend against an empty database, confirm `/api/health` is `Healthy` and the log says
   `Reference data ready`.
2. Bootstrap the first admin, sign in from the desktop client.
3. Connect a real Android phone, run **Scan**, **ADB Tools → List devices**, one diagnostic and one
   `adb.reboot`.
4. Open **Service Operations**: the operation must be `Completed` with a real exit code.
5. Create a ticket with the diagnostic report attached, reply as support, confirm both sides see it.
6. Open **License Activation**: verify, deactivate and activate again.
7. Toggle the language to العربية and confirm the layout mirrors right-to-left.
