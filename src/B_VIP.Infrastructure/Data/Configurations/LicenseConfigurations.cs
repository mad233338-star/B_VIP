using B_VIP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace B_VIP.Infrastructure.Data.Configurations
{
    public sealed class LicensePlanConfiguration : IEntityTypeConfiguration<LicensePlan>
    {
        public void Configure(EntityTypeBuilder<LicensePlan> builder)
        {
            builder.ToTable("license_plans");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32).IsRequired();
            builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            builder.Property(x => x.NameAr).HasColumnName("name_ar").HasMaxLength(128);
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(x => x.DurationDays).HasColumnName("duration_days");
            builder.Property(x => x.MaxActivations).HasColumnName("max_activations");
            builder.Property(x => x.Price).HasColumnName("price").HasPrecision(12, 2);
            builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(8);
            builder.Property(x => x.IsLifetime).HasColumnName("is_lifetime");
            builder.Property(x => x.IsActive).HasColumnName("is_active");
            builder.Property(x => x.SortOrder).HasColumnName("sort_order");
            builder.HasIndex(x => x.Code).IsUnique();
        }
    }

    public sealed class LicenseConfiguration : IEntityTypeConfiguration<License>
    {
        public void Configure(EntityTypeBuilder<License> builder)
        {
            builder.ToTable("licenses");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.LicenseKey).HasColumnName("license_key").HasMaxLength(32).IsRequired();
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.LicensePlanId).HasColumnName("license_plan_id");
            builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.StartsAt).HasColumnName("starts_at");
            builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            builder.Property(x => x.MaxActivations).HasColumnName("max_activations");
            builder.Property(x => x.CurrentActivations).HasColumnName("current_activations");
            builder.Property(x => x.SuspensionReason).HasColumnName("suspension_reason").HasMaxLength(512);
            builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            builder.Property(x => x.AdminNotes).HasColumnName("admin_notes").HasMaxLength(2000);
            builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
            builder.HasIndex(x => x.LicenseKey).IsUnique();
            builder.HasIndex(x => new { x.UserId, x.Status });
            builder.HasIndex(x => x.Status);
            builder.HasOne(x => x.User).WithMany(x => x.Licenses).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.LicensePlan).WithMany(x => x.Licenses).HasForeignKey(x => x.LicensePlanId).OnDelete(DeleteBehavior.Restrict);
        }
    }

    public sealed class LicenseActivationConfiguration : IEntityTypeConfiguration<LicenseActivation>
    {
        public void Configure(EntityTypeBuilder<LicenseActivation> builder)
        {
            builder.ToTable("license_activations");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.LicenseId).HasColumnName("license_id");
            builder.Property(x => x.DeviceFingerprint).HasColumnName("device_fingerprint").HasMaxLength(128).IsRequired();
            builder.Property(x => x.DeviceName).HasColumnName("device_name").HasMaxLength(256).IsRequired();
            builder.Property(x => x.Platform).HasColumnName("platform").HasMaxLength(64);
            builder.Property(x => x.AppVersion).HasColumnName("app_version").HasMaxLength(32);
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(x => x.DeactivatedReason).HasColumnName("deactivated_reason").HasMaxLength(512);
            builder.HasIndex(x => new { x.LicenseId, x.DeviceFingerprint });
            builder.HasOne(x => x.License).WithMany(x => x.Activations).HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class LicenseVerificationEventConfiguration : IEntityTypeConfiguration<LicenseVerificationEvent>
    {
        public void Configure(EntityTypeBuilder<LicenseVerificationEvent> builder)
        {
            builder.ToTable("license_verification_events");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.LicenseId).HasColumnName("license_id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.Result).HasColumnName("result").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(1024);
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.Property(x => x.ClientVersion).HasColumnName("client_version").HasMaxLength(32);
            builder.HasIndex(x => new { x.LicenseId, x.PerformedUtc });
            builder.HasOne(x => x.License).WithMany(x => x.VerificationEvents).HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class LicenseFeatureEntitlementConfiguration : IEntityTypeConfiguration<LicenseFeatureEntitlement>
    {
        public void Configure(EntityTypeBuilder<LicenseFeatureEntitlement> builder)
        {
            builder.ToTable("license_feature_entitlements");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.LicenseId).HasColumnName("license_id");
            builder.Property(x => x.FeatureKey).HasColumnName("feature_key").HasMaxLength(64).IsRequired();
            builder.Property(x => x.Value).HasColumnName("value").HasMaxLength(256);
            builder.HasIndex(x => new { x.LicenseId, x.FeatureKey }).IsUnique();
            builder.HasOne(x => x.License).WithMany(x => x.Entitlements).HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
