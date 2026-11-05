using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace B_VIP.Infrastructure.Data
{
    /// <summary>
    /// Chooses the database provider once, at startup: PostgreSQL when the configured server
    /// actually answers, the local SQLite file when it does not (PostgreSQL not installed, not
    /// running, wrong credentials or a missing database). The application never fails to start
    /// just because PostgreSQL is unavailable on that machine.
    /// </summary>
    public static class DatabaseProviderChoice
    {
        public const string Postgres = "postgres";
        public const string Sqlite = "sqlite";

        private const int ProbeTimeoutSeconds = 3;

        /// <summary>
        /// Reads <c>ConnectionStrings:Sqlite</c> (default <c>Data Source=b_vip.db</c>) and resolves a
        /// relative file against the application folder, so the database always sits next to the
        /// executable no matter what the working directory is.
        /// </summary>
        public static string ResolveSqliteConnectionString(IConfiguration configuration)
        {
            string? configured = configuration.GetConnectionString("Sqlite");
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = "Data Source=b_vip.db";
            }

            try
            {
                var builder = new SqliteConnectionStringBuilder(configured);
                if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
                    builder.DataSource != ":memory:" &&
                    !Path.IsPathRooted(builder.DataSource))
                {
                    builder.DataSource = Path.Combine(AppContext.BaseDirectory, builder.DataSource);
                }

                return builder.ConnectionString;
            }
            catch (FormatException)
            {
                // Not a parseable SQLite connection string: fall back to the default file.
                return $"Data Source={Path.Combine(AppContext.BaseDirectory, "b_vip.db")}";
            }
        }

        /// <summary>
        /// Decides the provider for this start: <c>Database:Provider</c> can force the choice
        /// (<c>postgres</c>, <c>sqlite</c>); otherwise the configured PostgreSQL server is probed
        /// with a short timeout. Any failure - refused connection, timeout, bad credentials,
        /// unknown database - means the machine falls back to SQLite instead of stopping the
        /// backend.
        /// </summary>
        public static async Task<(string Provider, string Detail)> DecideAsync(
            IConfiguration configuration, CancellationToken ct = default)
        {
            string? forced = configuration["Database:Provider"];
            if (string.Equals(forced, Postgres, StringComparison.OrdinalIgnoreCase))
            {
                return (Postgres, "Forced by Database:Provider.");
            }

            if (string.Equals(forced, Sqlite, StringComparison.OrdinalIgnoreCase))
            {
                return (Sqlite, "Forced by Database:Provider.");
            }

            string? connectionString = configuration.GetConnectionString("Default");
            if (LooksLikeSqlite(connectionString))
            {
                return (Sqlite, "The connection string already points at SQLite.");
            }

            NpgsqlConnectionStringBuilder probe;
            try
            {
                probe = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Timeout = ProbeTimeoutSeconds,
                    CommandTimeout = ProbeTimeoutSeconds
                };
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return (Sqlite, $"The PostgreSQL connection string could not be read ({ex.Message}).");
            }

            if (string.IsNullOrWhiteSpace(probe.Host))
            {
                return (Sqlite, "No PostgreSQL host is configured.");
            }

            try
            {
                await using var connection = new NpgsqlConnection(probe.ConnectionString);
                await connection.OpenAsync(ct);

                return (Postgres,
                    $"PostgreSQL answered at {probe.Host}:{probe.Port} (database '{probe.Database}').");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Sqlite,
                    $"PostgreSQL at {probe.Host}:{probe.Port} is unreachable ({ex.GetType().Name}: {ex.Message}); " +
                    "falling back to the local SQLite file.");
            }
        }

        private static bool LooksLikeSqlite(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return true;
            }

            return connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) ||
                   connectionString.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase) ||
                   connectionString.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase);
        }
    }
}
