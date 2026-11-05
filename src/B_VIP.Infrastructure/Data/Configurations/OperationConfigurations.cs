using B_VIP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace B_VIP.Infrastructure.Data.Configurations
{
    public sealed class DeviceServiceOperationConfiguration : IEntityTypeConfiguration<DeviceServiceOperation>
    {
        public void Configure(EntityTypeBuilder<DeviceServiceOperation> builder)
        {
            builder.ToTable("device_service_operations");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64).IsRequired();
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.OperationType).HasColumnName("operation_type").HasMaxLength(96).IsRequired();
            builder.Property(x => x.TargetDeviceId).HasColumnName("target_device_id").HasMaxLength(128);
            builder.Property(x => x.TargetDeviceDescription).HasColumnName("target_device_description").HasMaxLength(256);
            builder.Property(x => x.LicenseId).HasColumnName("license_id");
            builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.ToolPath).HasColumnName("tool_path").HasMaxLength(512);
            builder.Property(x => x.ToolVersion).HasColumnName("tool_version").HasMaxLength(64);
            builder.Property(x => x.SuccessCondition).HasColumnName("success_condition").HasMaxLength(64);
            builder.Property(x => x.OutputText).HasColumnName("output_text").HasMaxLength(200_000);
            builder.Property(x => x.ErrorText).HasColumnName("error_text").HasMaxLength(100_000);
            builder.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(2000);
            builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(1024);
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(x => x.ClientVersion).HasColumnName("client_version").HasMaxLength(32);
            builder.HasIndex(x => new { x.UserId, x.QueuedUtc });
            builder.HasIndex(x => new { x.Status, x.QueuedUtc });
            builder.HasIndex(x => x.CorrelationId);
            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(x => x.DiagnosticReport).WithOne()
                .HasForeignKey<DeviceServiceOperation>(x => x.DiagnosticReportId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }

    public sealed class OperationEventConfiguration : IEntityTypeConfiguration<OperationEvent>
    {
        public void Configure(EntityTypeBuilder<OperationEvent> builder)
        {
            builder.ToTable("operation_events");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.OperationId).HasColumnName("operation_id");
            builder.Property(x => x.Level).HasColumnName("level").HasMaxLength(16);
            builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(2000);
            builder.Property(x => x.Data).HasColumnName("data").HasMaxLength(8000);
            builder.HasIndex(x => new { x.OperationId, x.OccurredUtc });
            builder.HasOne(x => x.Operation).WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class DiagnosticReportConfiguration : IEntityTypeConfiguration<DiagnosticReport>
    {
        public void Configure(EntityTypeBuilder<DiagnosticReport> builder)
        {
            builder.ToTable("diagnostic_reports");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.OperationId).HasColumnName("operation_id");
            builder.Property(x => x.DeviceId).HasColumnName("device_id").HasMaxLength(128);
            builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(256);
            builder.Property(x => x.Module).HasColumnName("module").HasMaxLength(64);
            builder.Property(x => x.ContentJson).HasColumnName("content_json").IsRequired();
            builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.HasIndex(x => new { x.UserId, x.CreatedUtc });
            builder.HasIndex(x => x.Module);
            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class ReportAttachmentConfiguration : IEntityTypeConfiguration<ReportAttachment>
    {
        public void Configure(EntityTypeBuilder<ReportAttachment> builder)
        {
            builder.ToTable("report_attachments");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.DiagnosticReportId).HasColumnName("diagnostic_report_id");
            builder.Property(x => x.StoredFileId).HasColumnName("stored_file_id");
            builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(256);
            builder.HasOne(x => x.DiagnosticReport).WithMany().HasForeignKey(x => x.DiagnosticReportId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.StoredFile).WithMany().HasForeignKey(x => x.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
