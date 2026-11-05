namespace B_VIP.Infrastructure.Options
{
    /// <summary>Authentication, token and lockout settings (bound from the "Auth" section).</summary>
    public sealed class AuthOptions
    {
        public const string SectionName = "Auth";

        /// <summary>HMAC signing key for access tokens. Supplied through configuration/secrets, never hard coded.</summary>
        public string SigningKey { get; set; } = string.Empty;

        public string Issuer { get; set; } = "B_VIP.Backend";

        public string Audience { get; set; } = "B_VIP.Clients";

        public int AccessTokenMinutes { get; set; } = 60;

        public int RefreshTokenDays { get; set; } = 14;

        public int PasswordResetMinutes { get; set; } = 30;

        public int MaxFailedAccessAttempts { get; set; } = 5;

        public int LockoutMinutes { get; set; } = 15;

        public int PasswordResetPerEmailPerHour { get; set; } = 5;
    }

    /// <summary>Server side storage locations (bound from the "Storage" section).</summary>
    public sealed class StorageOptions
    {
        public const string SectionName = "Storage";

        public string DataDirectory { get; set; } = "data";

        public string BackupDirectory { get; set; } = "data/backups";

        public string UploadDirectory { get; set; } = "data/uploads";

        public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>Optional explicit paths to the PostgreSQL dump/restore tools. Falls back to PATH.</summary>
        public string? PgDumpPath { get; set; }

        public string? PgRestorePath { get; set; }

        public int ReportRetentionDays { get; set; } = 180;

        public int AuditRetentionDays { get; set; } = 730;
    }

    /// <summary>License policy (bound from the "License" section).</summary>
    public sealed class LicenseOptions
    {
        public const string SectionName = "License";

        /// <summary>Offline grace period applied when the server cannot be reached.</summary>
        public int OfflineGraceHours { get; set; } = 72;

        public bool AllowOfflineWork { get; set; }

        public int VerificationCacheMinutes { get; set; } = 15;
    }

    /// <summary>Cryptographic material for secrets stored at rest (bound from the "Security" section).</summary>
    public sealed class SecurityOptions
    {
        public const string SectionName = "Security";

        /// <summary>Base64 encoded 32 byte key used to encrypt integration secrets in the database.</summary>
        public string SecretEncryptionKey { get; set; } = string.Empty;
    }

    /// <summary>First-run bootstrap settings (bound from the "Bootstrap" section).</summary>
    public sealed class BootstrapOptions
    {
        public const string SectionName = "Bootstrap";

        /// <summary>One-time setup token required to create the first Super Admin.</summary>
        public string FirstAdminToken { get; set; } = string.Empty;

        public bool AllowFirstAdminBootstrap { get; set; } = true;

        /// <summary>
        /// Account seeded on the very first start so a fresh installation can be signed into
        /// without a manual bootstrap call. It is created only while the users table is empty and
        /// only when a password is configured here.
        /// </summary>
        public string DefaultAdminUserName { get; set; } = "admin";

        public string DefaultAdminEmail { get; set; } = "admin@b-vip.local";

        public string DefaultAdminPassword { get; set; } = string.Empty;
    }
}
