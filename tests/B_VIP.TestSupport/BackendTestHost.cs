using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Licenses;
using B_VIP.Core;
using B_VIP.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Threading.RateLimiting;

namespace B_VIP.TestSupport
{
    /// <summary>Connection and credential settings shared by every backend test host.</summary>
    public static class TestEnvironment
    {
        public const string Host = "127.0.0.1";
        public const int Port = 5433;
        public const string PgUserName = "postgres";

        /// <summary>32+ characters as required by the production security validation.</summary>
        public const string SigningKey = "TEST-SIGNING-KEY-0123456789-abcdefghijklmnopqrstuvwxyz-0123456789";
        public const string SecretEncryptionKey = "TEST-SECRET-ENCRYPTION-KEY-32B-YTES-0123456789";
        public const string BootstrapToken = "TEST-BOOTSTRAP-TOKEN-0123456789";

        public const string AdminUserName = "root.admin";
        public const string AdminPassword = "Admin!Passw0rd-2026";
        public const string UserPassword = "Test!Passw0rd-2026";

        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static string ConnectionString(string database)
            => $"Host={Host};Port={Port};Database={database};Username={PgUserName};Password=;Pooling=true;Timeout=30;Command Timeout=180";

        /// <summary>
        /// Gives every run a clean schema. The database itself is created once and then emptied with
        /// DROP SCHEMA / CREATE SCHEMA, which is far faster than dropping a database on a workstation
        /// filesystem and still leaves migrations and seed data to run from scratch.
        /// </summary>
        public static async Task RecreateDatabaseAsync(string database)
        {
            if (string.IsNullOrWhiteSpace(database) || database.Any(c => !(char.IsLower(c) || char.IsDigit(c) || c == '_')))
            {
                throw new ArgumentException("Database names must contain only lowercase letters, digits and '_'.", nameof(database));
            }

            await using (var admin = new NpgsqlConnection(ConnectionString("postgres")))
            {
                await admin.OpenAsync();
                await using NpgsqlCommand exists = admin.CreateCommand();
                exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
                exists.Parameters.AddWithValue("name", database);
                bool present = await exists.ExecuteScalarAsync() is not null;

                if (!present)
                {
                    await using NpgsqlCommand create = admin.CreateCommand();
                    create.CommandText = $"CREATE DATABASE \"{database}\"";
                    await create.ExecuteNonQueryAsync();
                }
            }

            await using var connection = new NpgsqlConnection(ConnectionString(database));
            await connection.OpenAsync();
            await using NpgsqlCommand reset = connection.CreateCommand();
            reset.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            await reset.ExecuteNonQueryAsync();
        }
    }

    /// <summary>An account created by a test run; the password is known so the test can sign in as it.</summary>
    public sealed record TestUser(Guid Id, string UserName, string Password);

    public static class HttpTestExtensions
    {
        /// <summary>Parses the standard <c>{"error":{...}}</c> envelope every failing endpoint returns.</summary>
        public static async Task<ApiError> ReadErrorAsync(this HttpResponseMessage response)
        {
            string body = await response.Content.ReadAsStringAsync();
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out JsonElement error))
                {
                    string code = error.TryGetProperty("code", out JsonElement codeElement) ? codeElement.GetString() ?? string.Empty : string.Empty;
                    string message = error.TryGetProperty("message", out JsonElement messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
                    string? correlationId = error.TryGetProperty("correlationId", out JsonElement correlationElement) ? correlationElement.GetString() : null;
                    return new ApiError(code, message, correlationId);
                }
            }
            catch (JsonException)
            {
                // Not a JSON body: fall through and report the raw text.
            }

            return new ApiError(string.Empty, body, null);
        }

        public static async Task<T?> ReadJsonAsync<T>(this HttpResponseMessage response)
        {
            string body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(body, TestEnvironment.Json);
        }

        public static async Task<T> ReadJsonRequiredAsync<T>(this HttpResponseMessage response)
        {
            string body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(body, TestEnvironment.Json)
                   ?? throw new InvalidOperationException($"The response body deserialized to null: {body}");
        }
    }

    /// <summary>
    /// Boots the real backend (real middleware pipeline, real EF Core against a disposable
    /// PostgreSQL database) with deterministic test settings. Each concrete factory owns its own
    /// database so test projects never interfere with each other.
    /// </summary>
    public abstract class BackendTestHost : WebApplicationFactory<Program>
    {
        private readonly SemaphoreSlim _initGate = new(1, 1);
        private readonly ConcurrentDictionary<string, Lazy<Task<SessionResponse>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool _initialized;

        protected BackendTestHost(string databaseName, bool strictRateLimits = false)
        {
            DatabaseName = databaseName;
            StrictRateLimits = strictRateLimits;
        }

        public string DatabaseName { get; }

        /// <summary>When true, the production rate limits stay active (used by the rate-limit tests).</summary>
        public bool StrictRateLimits { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // The suites always talk to real PostgreSQL: the startup fallback never applies here.
            builder.UseSetting("Database:Provider", "postgres");
            builder.UseSetting("ConnectionStrings:Default", TestEnvironment.ConnectionString(DatabaseName));
            builder.UseSetting("Auth:SigningKey", TestEnvironment.SigningKey);
            builder.UseSetting("Security:SecretEncryptionKey", TestEnvironment.SecretEncryptionKey);
            builder.UseSetting("Bootstrap:FirstAdminToken", TestEnvironment.BootstrapToken);
            builder.UseSetting("Bootstrap:AllowFirstAdminBootstrap", "true");
            // The suites create their own administrator through the one-time bootstrap call, so the
            // first-start default admin seed (shipped for real installations) stays disabled here.
            builder.UseSetting("Bootstrap:DefaultAdminPassword", string.Empty);


            if (!StrictRateLimits)
            {
                // The production limits (10 auth calls and 600 requests per minute per address) would
                // throttle a full test run, so the test host keeps the same limiter shape but far
                // higher permits. The strict limits are exercised by the dedicated rate-limit tests.
                builder.ConfigureServices(services =>
                {
                    // Drop the limiter options registered by Program so the named policies are not
                    // declared twice; only the relaxed test configuration remains.
                    services.RemoveAll<IConfigureOptions<RateLimiterOptions>>();
                    services.RemoveAll<IPostConfigureOptions<RateLimiterOptions>>();

                    services.AddRateLimiter(options =>
                    {
                        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                            RateLimitPartition.GetFixedWindowLimiter("global", _ =>
                                new FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = 100_000,
                                    Window = TimeSpan.FromMinutes(1),
                                    QueueLimit = 0,
                                    AutoReplenishment = true
                                }));

                        options.AddFixedWindowLimiter("auth", limiter =>
                        {
                            limiter.PermitLimit = 100_000;
                            limiter.Window = TimeSpan.FromMinutes(1);
                            limiter.QueueLimit = 0;
                            limiter.AutoReplenishment = true;
                        });
                    });
                });
            }
        }

        /// <summary>
        /// Creates the database, boots the host, waits for the schema and reference data, then runs
        /// the one-time Super Admin bootstrap. Idempotent; safe to call from every test.
        /// </summary>
        public async Task EnsureInitializedAsync()
        {
            if (_initialized) return;

            await _initGate.WaitAsync();
            try
            {
                if (_initialized) return;
                await InitializeCoreAsync();
                _initialized = true;
            }
            finally
            {
                _initGate.Release();
            }
        }

        private async Task InitializeCoreAsync()
        {
            await TestEnvironment.RecreateDatabaseAsync(DatabaseName);

            // Creating a client boots the host. base.CreateClient is used here so initialization
            // never re-enters itself. The test factory stops the entry point right after the host is
            // built, so the suite applies the schema and reference data itself against the very same
            // database and container the API uses.
            _ = base.CreateClient();

            using (IServiceScope scope = Services.CreateScope())
            {
                B_VIPDbContext db = scope.ServiceProvider.GetRequiredService<B_VIPDbContext>();
                await db.Database.MigrateAsync();
                IDataSeeder seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
                await seeder.EnsureSeededAsync();
            }

            if (!await WaitUntilAsync(TimeSpan.FromSeconds(20), ProbeSchemaAsync))
            {
                throw new InvalidOperationException(
                    $"The test database '{DatabaseName}' did not reach the migrated, seeded state.");
            }

            HttpClient anonymous = base.CreateClient();
            HttpResponseMessage bootstrap = await anonymous.PostAsJsonAsync("/api/bootstrap/first-admin", new
            {
                token = TestEnvironment.BootstrapToken,
                userName = TestEnvironment.AdminUserName,
                email = "root@b-vip.test",
                password = TestEnvironment.AdminPassword
            });

            if (bootstrap.StatusCode is not (System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Conflict))
            {
                string body = await bootstrap.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"First-admin bootstrap failed: {(int)bootstrap.StatusCode} {body}");
            }
        }

        private async Task<bool> ProbeSchemaAsync()
        {
            try
            {
                using IServiceScope scope = Services.CreateScope();
                B_VIPDbContext db = scope.ServiceProvider.GetRequiredService<B_VIPDbContext>();
                List<string> applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
                List<string> known = db.Database.GetMigrations().ToList();
                int roles = await db.Roles.CountAsync();
                return known.Count > 0 && known.All(applied.Contains) && roles >= RoleNames.All.Length;
            }
            catch (Exception)
            {
                // Database still starting or migrating; retry until the deadline.
                return false;
            }
        }

        private static async Task<bool> WaitUntilAsync(TimeSpan timeout, Func<Task<bool>> probe)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await probe()) return true;
                await Task.Delay(500);
            }

            return await probe();
        }

        /// <summary>Signs in and caches the session (one login per account for the whole suite).</summary>
        public Task<SessionResponse> GetSessionAsync(string userName, string password)
            => _sessions.GetOrAdd(userName,
                _ => new Lazy<Task<SessionResponse>>(() => LoginCoreAsync(userName, password))).Value;

        private async Task<SessionResponse> LoginCoreAsync(string userName, string password)
        {
            await EnsureInitializedAsync();
            HttpClient client = CreateClient();
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Login for '{userName}' failed: {(int)response.StatusCode} {body}");
            }

            return JsonSerializer.Deserialize<SessionResponse>(body, TestEnvironment.Json)
                   ?? throw new InvalidOperationException($"Login for '{userName}' returned an empty body.");
        }

        public Task<SessionResponse> AdminSessionAsync()
            => GetSessionAsync(TestEnvironment.AdminUserName, TestEnvironment.AdminPassword);

        /// <summary>A bare client (no Authorization header). Blocks until the host is initialized.</summary>
        public new HttpClient CreateClient()
        {
            EnsureInitializedAsync().GetAwaiter().GetResult();
            return base.CreateClient();
        }

        /// <summary>A client carrying the given session's bearer token.</summary>
        public HttpClient CreateClient(SessionResponse session)
        {
            HttpClient client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
            return client;
        }

        public async Task<HttpClient> CreateAdminClientAsync() => CreateClient(await AdminSessionAsync());

        /// <summary>Creates a fresh unique account through the API and returns its credentials.</summary>
        public async Task<TestUser> CreateUserAsync(string? roleName = null)
        {
            HttpClient admin = await CreateAdminClientAsync();
            string userName = "t" + Guid.NewGuid().ToString("N")[..12];
            object payload = roleName is null
                ? new { userName, email = $"{userName}@b-vip.test", displayName = $"Test {userName}", password = TestEnvironment.UserPassword, roles = (IReadOnlyList<string>?)null }
                : (object)new { userName, email = $"{userName}@b-vip.test", displayName = $"Test {userName}", password = TestEnvironment.UserPassword, roles = new[] { roleName } };

            HttpResponseMessage response = await admin.PostAsJsonAsync("/api/users", payload);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"User creation failed: {(int)response.StatusCode} {body}");
            }

            UserSummaryDto dto = JsonSerializer.Deserialize<UserSummaryDto>(body, TestEnvironment.Json)
                ?? throw new InvalidOperationException($"User creation returned an empty body: {body}");
            return new TestUser(dto.Id, dto.UserName, TestEnvironment.UserPassword);
        }

        /// <summary>Creates a fresh account and returns it together with a valid session.</summary>
        public async Task<(TestUser User, SessionResponse Session)> CreateUserSessionAsync(string? roleName = null)
        {
            TestUser user = await CreateUserAsync(roleName);
            SessionResponse session = await GetSessionAsync(user.UserName, user.Password);
            return (user, session);
        }

        /// <summary>Issues an active license for the user through the admin API.</summary>
        public async Task<LicenseDto> CreateLicenseAsync(Guid userId, string planCode = "trial")
        {
            HttpClient admin = await CreateAdminClientAsync();
            HttpResponseMessage response = await admin.PostAsJsonAsync("/api/licenses", new { userId, planCode });
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"License creation failed: {(int)response.StatusCode} {body}");
            }

            return JsonSerializer.Deserialize<LicenseDto>(body, TestEnvironment.Json)
                ?? throw new InvalidOperationException($"License creation returned an empty body: {body}");
        }
    }
}
