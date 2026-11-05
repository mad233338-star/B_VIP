using B_VIP.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace B_VIP.Infrastructure.Data
{
    /// <summary>
    /// The single EF Core model for B_VIP Tool (PostgreSQL).
    /// The database is the source of truth; in-memory collections are never used for production data.
    /// </summary>
    public class B_VIPDbContext : DbContext
    {
        public B_VIPDbContext(DbContextOptions<B_VIPDbContext> options) : base(options)
        {
        }

        // Accounts
        public DbSet<User> Users => Set<User>();
        public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
        public DbSet<Role> Roles => Set<Role>();
        public DbSet<Permission> Permissions => Set<Permission>();
        public DbSet<UserRole> UserRoles => Set<UserRole>();
        public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
        public DbSet<UserSession> UserSessions => Set<UserSession>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<PasswordResetRequest> PasswordResetRequests => Set<PasswordResetRequest>();
        public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
        public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

        // Licensing
        public DbSet<LicensePlan> LicensePlans => Set<LicensePlan>();
        public DbSet<License> Licenses => Set<License>();
        public DbSet<LicenseActivation> LicenseActivations => Set<LicenseActivation>();
        public DbSet<LicenseVerificationEvent> LicenseVerificationEvents => Set<LicenseVerificationEvent>();
        public DbSet<LicenseFeatureEntitlement> LicenseFeatureEntitlements => Set<LicenseFeatureEntitlement>();

        // Operations
        public DbSet<DeviceServiceOperation> DeviceServiceOperations => Set<DeviceServiceOperation>();
        public DbSet<OperationEvent> OperationEvents => Set<OperationEvent>();
        public DbSet<DiagnosticReport> DiagnosticReports => Set<DiagnosticReport>();
        public DbSet<ReportAttachment> ReportAttachments => Set<ReportAttachment>();

        // Support
        public DbSet<SupportCategory> SupportCategories => Set<SupportCategory>();
        public DbSet<ServiceTicket> ServiceTickets => Set<ServiceTicket>();
        public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
        public DbSet<TicketAssignment> TicketAssignments => Set<TicketAssignment>();
        public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();

        // System
        public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
        public DbSet<MaintenanceSetting> MaintenanceSettings => Set<MaintenanceSetting>();
        public DbSet<IntegrationConfiguration> IntegrationConfigurations => Set<IntegrationConfiguration>();
        public DbSet<IntegrationHealthCheck> IntegrationHealthChecks => Set<IntegrationHealthCheck>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<SoftwareRelease> SoftwareReleases => Set<SoftwareRelease>();
        public DbSet<DownloadableReleaseMetadata> DownloadableReleaseMetadata => Set<DownloadableReleaseMetadata>();
        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
        public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();
        public DbSet<SystemHealthEvent> SystemHealthEvents => Set<SystemHealthEvent>();
        public DbSet<DocumentMetadata> DocumentMetadata => Set<DocumentMetadata>();
        public DbSet<StoredFileMetadata> StoredFileMetadata => Set<StoredFileMetadata>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.ApplyConfigurationsFromAssembly(typeof(B_VIPDbContext).Assembly);

            // Every table carries UTC timestamps only. Required timestamps (created/updated) default
            // to the database clock; nullable timestamps such as RevokedUtc or CompletedUtc must stay
            // NULL until the event actually happens, so they never receive a default.
            // The PostgreSQL clock function is provider specific: on the local SQLite fallback the
            // schema is generated from this model, so no default is set there.
            bool isSqlite = Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

            if (!isSqlite)
            {
                foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
                {
                    foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in entity.GetProperties())
                    {
                        if (property.ClrType == typeof(DateTime) && !property.IsNullable)
                        {
                            property.SetDefaultValueSql("timezone('utc', now())");
                        }
                    }
                }
            }
        }
    }
}
