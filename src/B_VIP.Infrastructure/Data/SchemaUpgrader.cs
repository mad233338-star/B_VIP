using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace B_VIP.Infrastructure.Data
{
    /// <summary>
    /// Brings an existing database up to the current account model. The technician onboarding and
    /// HWID binding added four columns to <c>users</c>; a fresh SQLite file is created from the EF
    /// model (which already contains them), while an installation that was created earlier - and
    /// the PostgreSQL schema, which follows the shipped migrations - receives them here.
    /// Every statement is idempotent, so the upgrader is safe to run on every start.
    /// </summary>
    public static class SchemaUpgrader
    {
        private static readonly (string Name, string Sqlite, string Postgres)[] AccountColumns =
        [
            ("hardware_id", "TEXT NULL", "character varying(128) NULL"),
            ("hardware_id_reset_count", "INTEGER NOT NULL DEFAULT 0", "integer NOT NULL DEFAULT 0"),
            ("email_verification_token_hash", "TEXT NULL", "character varying(128) NULL"),
            ("email_verification_sent_utc", "TEXT NULL", "timestamp with time zone NULL"),
            ("email_verification_code_hash", "TEXT NULL", "character varying(128) NULL"),
            ("email_verification_code_expires_utc", "TEXT NULL", "timestamp with time zone NULL")
        ];

        public static async Task ApplyAsync(B_VIPDbContext db, CancellationToken ct = default)
        {
            bool sqlite = db.Database.IsSqlite();

            HashSet<string> existing = sqlite
                ? await ReadSqliteColumnsAsync(db, "users", ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string name, string sqliteType, string postgresType) in AccountColumns)
            {
                if (sqlite)
                {
                    if (existing.Contains(name))
                    {
                        continue;
                    }

                    // DDL identifiers cannot be SQL parameters; both values come from the fixed
                    // AccountColumns table above, never from a request.
                    await db.Database.ExecuteSqlRawAsync(
                        "ALTER TABLE users ADD COLUMN " + name + " " + sqliteType + ";", ct);
                }
                else
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "ALTER TABLE users ADD COLUMN IF NOT EXISTS " + name + " " + postgresType + ";", ct);
                }
            }
        }

        private static async Task<HashSet<string>> ReadSqliteColumnsAsync(B_VIPDbContext db, string table, CancellationToken ct)
        {
            HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);

            DbConnection connection = db.Database.GetDbConnection();
            bool opened = connection.State != System.Data.ConnectionState.Open;
            if (opened)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info('{table}');";

                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    // Column 1 of PRAGMA table_info is the column name.
                    if (!reader.IsDBNull(1))
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
            }
            finally
            {
                if (opened)
                {
                    await connection.CloseAsync();
                }
            }

            return columns;
        }
    }
}
