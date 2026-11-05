using B_VIP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace B_VIP.Infrastructure.Data.Configurations
{
    public sealed class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.ToTable("users");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(64).IsRequired();
            builder.Property(x => x.NormalizedUserName).HasColumnName("normalized_user_name").HasMaxLength(64).IsRequired();
            builder.Property(x => x.Email).HasColumnName("email").HasMaxLength(256).IsRequired();
            builder.Property(x => x.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(256).IsRequired();
            builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
            builder.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(256).IsRequired();
            builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);
            builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            builder.Property(x => x.HardwareId).HasColumnName("hardware_id").HasMaxLength(128);
            builder.Property(x => x.HardwareIdResetCount).HasColumnName("hardware_id_reset_count");
            builder.Property(x => x.EmailVerificationTokenHash).HasColumnName("email_verification_token_hash").HasMaxLength(128);
            builder.Property(x => x.EmailVerificationSentUtc).HasColumnName("email_verification_sent_utc");
            builder.Property(x => x.EmailVerificationCodeHash).HasColumnName("email_verification_code_hash").HasMaxLength(128);
            builder.Property(x => x.EmailVerificationCodeExpiresUtc).HasColumnName("email_verification_code_expires_utc");
            builder.HasIndex(x => x.NormalizedUserName).IsUnique();
            builder.HasIndex(x => x.NormalizedEmail).IsUnique();
            builder.HasIndex(x => x.Status);

            builder.HasOne(x => x.Profile)
                .WithOne(x => x.User)
                .HasForeignKey<UserProfile>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
    {
        public void Configure(EntityTypeBuilder<UserProfile> builder)
        {
            builder.ToTable("user_profiles");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(64);
            builder.Property(x => x.Company).HasColumnName("company").HasMaxLength(128);
            builder.Property(x => x.Country).HasColumnName("country").HasMaxLength(64);
            builder.Property(x => x.PreferredLanguage).HasColumnName("preferred_language").HasMaxLength(8);
            builder.Property(x => x.TimeZone).HasColumnName("time_zone").HasMaxLength(64);
            builder.Property(x => x.RegisteredUtc).HasColumnName("registered_utc");
            builder.Property(x => x.UpdatedUtc).HasColumnName("updated_utc");
            builder.HasIndex(x => x.UserId).IsUnique();
        }
    }

    public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
    {
        public void Configure(EntityTypeBuilder<Role> builder)
        {
            builder.ToTable("roles");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").HasMaxLength(64).IsRequired();
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
            builder.Property(x => x.IsSystem).HasColumnName("is_system");
            builder.HasIndex(x => x.NormalizedName).IsUnique();
        }
    }

    public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
    {
        public void Configure(EntityTypeBuilder<Permission> builder)
        {
            builder.ToTable("permissions");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(96).IsRequired();
            builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").HasMaxLength(96).IsRequired();
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
            builder.Property(x => x.Group).HasColumnName("group_name").HasMaxLength(64).IsRequired();
            builder.HasIndex(x => x.NormalizedName).IsUnique();
        }
    }

    public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
    {
        public void Configure(EntityTypeBuilder<UserRole> builder)
        {
            builder.ToTable("user_roles");
            builder.HasKey(x => new { x.UserId, x.RoleId });
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.RoleId).HasColumnName("role_id");
            builder.Property(x => x.AssignedUtc).HasColumnName("assigned_utc");
            builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id");
            builder.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Role).WithMany(x => x.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => x.RoleId);
        }
    }

    public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
    {
        public void Configure(EntityTypeBuilder<RolePermission> builder)
        {
            builder.ToTable("role_permissions");
            builder.HasKey(x => new { x.RoleId, x.PermissionId });
            builder.Property(x => x.RoleId).HasColumnName("role_id");
            builder.Property(x => x.PermissionId).HasColumnName("permission_id");
            builder.Property(x => x.GrantedUtc).HasColumnName("granted_utc");
            builder.HasOne(x => x.Role).WithMany(x => x.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => x.PermissionId);
        }
    }

    public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
    {
        public void Configure(EntityTypeBuilder<UserSession> builder)
        {
            builder.ToTable("user_sessions");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.SessionTokenHash).HasColumnName("session_token_hash").HasMaxLength(128).IsRequired();
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
            builder.HasIndex(x => x.SessionTokenHash).IsUnique();
            builder.HasIndex(x => new { x.UserId, x.ExpiresUtc });
            builder.HasOne(x => x.User).WithMany(x => x.Sessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
    {
        public void Configure(EntityTypeBuilder<RefreshToken> builder)
        {
            builder.ToTable("refresh_tokens");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.SessionId).HasColumnName("session_id");
            builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.HasIndex(x => x.TokenHash).IsUnique();
            builder.HasIndex(x => new { x.UserId, x.ExpiresUtc });
            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class PasswordResetRequestConfiguration : IEntityTypeConfiguration<PasswordResetRequest>
    {
        public void Configure(EntityTypeBuilder<PasswordResetRequest> builder)
        {
            builder.ToTable("password_reset_requests");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            builder.Property(x => x.RequestedIp).HasColumnName("requested_ip").HasMaxLength(64);
            builder.HasIndex(x => x.TokenHash).IsUnique();
            builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        }
    }

    public sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
    {
        public void Configure(EntityTypeBuilder<LoginAttempt> builder)
        {
            builder.ToTable("login_attempts");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(64);
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(128);
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.HasIndex(x => new { x.UserName, x.OccurredUtc });
            builder.HasIndex(x => x.OccurredUtc);
        }
    }

    public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
    {
        public void Configure(EntityTypeBuilder<SecurityEvent> builder)
        {
            builder.ToTable("security_events");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(64);
            builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(2000);
            builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(32);
            builder.HasIndex(x => x.OccurredUtc);
        }
    }
}
