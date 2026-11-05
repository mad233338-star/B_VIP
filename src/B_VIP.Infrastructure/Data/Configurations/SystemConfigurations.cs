using B_VIP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace B_VIP.Infrastructure.Data.Configurations
{
    public sealed class ApplicationSettingConfiguration : IEntityTypeConfiguration<ApplicationSetting>
    {
        public void Configure(EntityTypeBuilder<ApplicationSetting> builder)
        {
            builder.ToTable("application_settings");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(128).IsRequired();
            builder.Property(x => x.Value).HasColumnName("value").HasMaxLength(4000);
            builder.Property(x => x.IsSecret).HasColumnName("is_secret");
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
            builder.Property(x => x.Category).HasColumnName("category").HasMaxLength(64);
            builder.HasIndex(x => x.Key).IsUnique();
        }
    }

    public sealed class MaintenanceSettingConfiguration : IEntityTypeConfiguration<MaintenanceSetting>
    {
        public void Configure(EntityTypeBuilder<MaintenanceSetting> builder)
        {
            builder.ToTable("maintenance_settings");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.MaintenanceEnabled).HasColumnName("maintenance_enabled");
            builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(1024);
            builder.Property(x => x.StartsAt).HasColumnName("starts_at");
            builder.Property(x => x.EndsAt).HasColumnName("ends_at");
        }
    }

    public sealed class IntegrationConfigurationConfiguration : IEntityTypeConfiguration<IntegrationConfiguration>
    {
        public void Configure(EntityTypeBuilder<IntegrationConfiguration> builder)
        {
            builder.ToTable("integration_configurations");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
            builder.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.ToolPath).HasColumnName("tool_path").HasMaxLength(512);
            builder.Property(x => x.BaseUrl).HasColumnName("base_url").HasMaxLength(512);
            builder.Property(x => x.ApiKeySecret).HasColumnName("api_key_secret").HasMaxLength(2000);
            builder.Property(x => x.ConfigurationJson).HasColumnName("configuration_json").HasMaxLength(8000);
            builder.HasIndex(x => x.Code).IsUnique();
        }
    }

    public sealed class IntegrationHealthCheckConfiguration : IEntityTypeConfiguration<IntegrationHealthCheck>
    {
        public void Configure(EntityTypeBuilder<IntegrationHealthCheck> builder)
        {
            builder.ToTable("integration_health_checks");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.IntegrationId).HasColumnName("integration_id");
            builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(1024);
            builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            builder.HasIndex(x => new { x.IntegrationId, x.CheckedUtc });
            builder.HasOne(x => x.Integration).WithMany(x => x.HealthChecks).HasForeignKey(x => x.IntegrationId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
    {
        public void Configure(EntityTypeBuilder<Notification> builder)
        {
            builder.ToTable("notifications");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(256);
            builder.Property(x => x.Body).HasColumnName("body").HasMaxLength(2000);
            builder.Property(x => x.Link).HasColumnName("link").HasMaxLength(512);
            builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(64);
            builder.HasIndex(x => new { x.UserId, x.ReadUtc });
            builder.HasIndex(x => x.CreatedUtc);
            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class SoftwareReleaseConfiguration : IEntityTypeConfiguration<SoftwareRelease>
    {
        public void Configure(EntityTypeBuilder<SoftwareRelease> builder)
        {
            builder.ToTable("software_releases");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Version).HasColumnName("version").HasMaxLength(32).IsRequired();
            builder.Property(x => x.ReleaseNotes).HasColumnName("release_notes").HasMaxLength(8000);
            builder.Property(x => x.MinSupportedVersion).HasColumnName("min_supported_version").HasMaxLength(32);
            builder.Property(x => x.DownloadUrl).HasColumnName("download_url").HasMaxLength(1024);
            builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            builder.HasIndex(x => x.Version).IsUnique();
        }
    }

    public sealed class DownloadableReleaseMetadataConfiguration : IEntityTypeConfiguration<DownloadableReleaseMetadata>
    {
        public void Configure(EntityTypeBuilder<DownloadableReleaseMetadata> builder)
        {
            builder.ToTable("downloadable_release_metadata");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.SoftwareReleaseId).HasColumnName("software_release_id");
            builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(256);
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(x => x.DownloadUrl).HasColumnName("download_url").HasMaxLength(1024);
            builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            builder.Property(x => x.Platform).HasColumnName("platform").HasMaxLength(64);
            builder.HasIndex(x => new { x.Platform, x.IsLatest });
            builder.HasOne(x => x.SoftwareRelease).WithMany(x => x.Artifacts).HasForeignKey(x => x.SoftwareReleaseId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
    {
        public void Configure(EntityTypeBuilder<AuditEvent> builder)
        {
            builder.ToTable("audit_events");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            builder.Property(x => x.ActorName).HasColumnName("actor_name").HasMaxLength(128);
            builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(128);
            builder.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(64);
            builder.Property(x => x.TargetId).HasColumnName("target_id").HasMaxLength(64);
            builder.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(4000);
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.HasIndex(x => x.OccurredUtc);
            builder.HasIndex(x => x.Action);
        }
    }

    public sealed class BackupRecordConfiguration : IEntityTypeConfiguration<BackupRecord>
    {
        public void Configure(EntityTypeBuilder<BackupRecord> builder)
        {
            builder.ToTable("backup_records");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.PerformedByUserId).HasColumnName("performed_by_user_id");
            builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            builder.Property(x => x.FilePath).HasColumnName("file_path").HasMaxLength(1024);
            builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            builder.Property(x => x.Checksum).HasColumnName("checksum").HasMaxLength(64);
            builder.Property(x => x.Error).HasColumnName("error").HasMaxLength(2000);
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.HasIndex(x => x.StartedUtc);
        }
    }

    public sealed class SystemHealthEventConfiguration : IEntityTypeConfiguration<SystemHealthEvent>
    {
        public void Configure(EntityTypeBuilder<SystemHealthEvent> builder)
        {
            builder.ToTable("system_health_events");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Component).HasColumnName("component").HasMaxLength(64);
            builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(1024);
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.HasIndex(x => new { x.Component, x.OccurredUtc });
        }
    }

    public sealed class DocumentMetadataConfiguration : IEntityTypeConfiguration<DocumentMetadata>
    {
        public void Configure(EntityTypeBuilder<DocumentMetadata> builder)
        {
            builder.ToTable("document_metadata");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(64);
            builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(256);
            builder.Property(x => x.Path).HasColumnName("path").HasMaxLength(512);
            builder.Property(x => x.Language).HasColumnName("language").HasMaxLength(8);
            builder.Property(x => x.Version).HasColumnName("version").HasMaxLength(32);
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.HasIndex(x => x.Key).IsUnique();
        }
    }

    public sealed class StoredFileMetadataConfiguration : IEntityTypeConfiguration<StoredFileMetadata>
    {
        public void Configure(EntityTypeBuilder<StoredFileMetadata> builder)
        {
            builder.ToTable("stored_file_metadata");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            builder.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(64);
            builder.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(256);
            builder.Property(x => x.StoredPath).HasColumnName("stored_path").HasMaxLength(1024);
            builder.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(128);
            builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            builder.HasIndex(x => new { x.Purpose, x.CreatedUtc });
        }
    }
}
