using B_VIP.Application.Interfaces;
using B_VIP.Core;
using B_VIP.Core.Entities;
using B_VIP.Core.Rules;
using B_VIP.Core.Security;
using B_VIP.Infrastructure.Data;
using B_VIP.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>
    /// Reference data bootstrap: roles, permissions, default plans, support categories,
    /// application settings and the built-in integrations. Runs on every start and is idempotent.
    /// </summary>
    public sealed class DataSeeder : IDataSeeder
    {
        private readonly B_VIPDbContext _db;
        private readonly IAuditLogger _audit;
        private readonly BootstrapOptions _bootstrap;
        private readonly ILogger<DataSeeder> _logger;

        public DataSeeder(B_VIPDbContext db, IAuditLogger audit, IOptions<BootstrapOptions> bootstrap, ILogger<DataSeeder> logger)
        {
            _db = db;
            _audit = audit;
            _bootstrap = bootstrap.Value;
            _logger = logger;
        }

        public async Task<SeedResult> EnsureSeededAsync(CancellationToken ct = default)
        {
            bool hadRoles = await _db.Roles.AnyAsync(ct);
            int roles = 0, permissions = 0, plans = 0, categories = 0, settings = 0, integrations = 0;

            // Permissions
            foreach (string permission in Permissions.All)
            {
                if (await _db.Permissions.AnyAsync(p => p.Name == permission, ct)) continue;

                _db.Permissions.Add(new Permission
                {
                    Id = Guid.NewGuid(),
                    Name = permission,
                    NormalizedName = permission.ToUpperInvariant(),
                    Group = Permissions.GroupOf(permission),
                    Description = null
                });
                permissions++;
            }

            await _db.SaveChangesAsync(ct);

            // Roles + default permission grants
            foreach (string roleName in RoleNames.All)
            {
                Role? role = await _db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == roleName.ToUpperInvariant(), ct);
                if (role is null)
                {
                    role = new Role
                    {
                        Id = Guid.NewGuid(),
                        Name = roleName,
                        NormalizedName = roleName.ToUpperInvariant(),
                        IsSystem = true,
                        Description = BuiltInRoleDescription(roleName),
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    _db.Roles.Add(role);
                    roles++;
                    await _db.SaveChangesAsync(ct);
                }

                if (await _db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id, ct)) continue;

                IReadOnlyList<string> granted = Permissions.DefaultForRole(roleName);
                List<Permission> permissionRows = await _db.Permissions
                    .Where(p => granted.Contains(p.Name))
                    .ToListAsync(ct);

                foreach (Permission permission in permissionRows)
                {
                    _db.RolePermissions.Add(new RolePermission
                    {
                        RoleId = role.Id,
                        PermissionId = permission.Id,
                        GrantedUtc = DateTime.UtcNow
                    });
                }

                await _db.SaveChangesAsync(ct);
            }

            // License plans
            foreach ((string code, string name, string nameAr, int? days, int activations, bool lifetime, int sort) plan in DefaultPlans())
            {
                if (await _db.LicensePlans.AnyAsync(p => p.Code == plan.code, ct)) continue;

                _db.LicensePlans.Add(new LicensePlan
                {
                    Id = Guid.NewGuid(),
                    Code = plan.code,
                    Name = plan.name,
                    NameAr = plan.nameAr,
                    Description = plan.name,
                    DurationDays = plan.days,
                    MaxActivations = plan.activations,
                    IsLifetime = plan.lifetime,
                    IsActive = true,
                    SortOrder = plan.sort,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                plans++;
            }

            // Support categories
            foreach ((string name, string nameAr, int sort) category in DefaultCategories())
            {
                if (await _db.SupportCategories.AnyAsync(c => c.Name == category.name, ct)) continue;

                _db.SupportCategories.Add(new SupportCategory
                {
                    Id = Guid.NewGuid(),
                    Name = category.name,
                    NameAr = category.nameAr,
                    IsActive = true,
                    SortOrder = category.sort
                });
                categories++;
            }

            // Application settings
            foreach ((string key, string? value, bool secret, string category, string description) setting in DefaultSettings())
            {
                if (await _db.ApplicationSettings.AnyAsync(s => s.Key == setting.key, ct)) continue;

                _db.ApplicationSettings.Add(new ApplicationSetting
                {
                    Id = Guid.NewGuid(),
                    Key = setting.key,
                    Value = setting.value,
                    IsSecret = setting.secret,
                    Category = setting.category,
                    Description = setting.description,
                    UpdatedUtc = DateTime.UtcNow
                });
                settings++;
            }

            // Built-in integrations
            foreach ((string code, string name, IntegrationKind kind, string description) integration in DefaultIntegrations())
            {
                if (await _db.IntegrationConfigurations.AnyAsync(i => i.Code == integration.code, ct)) continue;

                _db.IntegrationConfigurations.Add(new IntegrationConfiguration
                {
                    Id = Guid.NewGuid(),
                    Code = integration.code,
                    DisplayName = integration.name,
                    Kind = integration.kind,
                    IsEnabled = false,
                    IsConfigured = false,
                    ConfigurationJson = $"{{\"description\":\"{integration.description}\"}}",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                integrations++;
            }

            if (!await _db.MaintenanceSettings.AnyAsync(ct))
            {
                _db.MaintenanceSettings.Add(new MaintenanceSetting
                {
                    Id = Guid.NewGuid(),
                    MaintenanceEnabled = false,
                    UpdatedUtc = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(ct);

            await SeedDefaultAdminAsync(ct);
            await SeedMasterAdminAsync(ct);

            var result = new SeedResult(roles, permissions, plans, categories, settings, integrations, hadRoles);
            if (roles > 0 || plans > 0 || settings > 0)
            {
                _logger.LogInformation(
                    "Seeded reference data: {Roles} roles, {Permissions} permissions, {Plans} plans, {Categories} categories, {Settings} settings, {Integrations} integrations",
                    roles, permissions, plans, categories, settings, integrations);
            }

            return result;
        }

        /// <summary>
        /// Creates the initial Super Admin account on the very first start of a fresh installation,
        /// so the product can be signed into without a separate bootstrap call. It runs only while
        /// the users table is empty and only when a password is configured; invalid configuration
        /// is logged and skipped instead of failing startup.
        /// </summary>
        private async Task SeedDefaultAdminAsync(CancellationToken ct)
        {
            if (await _db.Users.AnyAsync(ct))
            {
                return;
            }

            string password = _bootstrap.DefaultAdminPassword?.Trim() ?? string.Empty;
            if (password.Length == 0)
            {
                return;
            }

            string userName = string.IsNullOrWhiteSpace(_bootstrap.DefaultAdminUserName)
                ? "admin"
                : _bootstrap.DefaultAdminUserName.Trim();
            string email = string.IsNullOrWhiteSpace(_bootstrap.DefaultAdminEmail)
                ? $"{userName}@b-vip.local"
                : _bootstrap.DefaultAdminEmail.Trim();

            if (!ValidationRules.IsValidUserName(userName))
            {
                _logger.LogWarning("Skipping the default admin seed: '{UserName}' is not a valid user name.", userName);
                return;
            }

            if (!ValidationRules.IsValidEmail(email))
            {
                _logger.LogWarning("Skipping the default admin seed: '{Email}' is not a valid e-mail address.", email);
                return;
            }

            (bool strong, string? policyError) = ValidationRules.ValidatePasswordStrength(password);
            if (!strong)
            {
                _logger.LogWarning(
                    "Skipping the default admin seed: the configured password does not meet the policy ({Error}).",
                    policyError ?? "password-policy");
                return;
            }

            Role superAdminRole = await _db.Roles
                .FirstAsync(r => r.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant(), ct);

            DateTime now = DateTime.UtcNow;
            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                NormalizedUserName = ValidationRules.NormalizeUserName(userName),
                Email = email,
                NormalizedEmail = ValidationRules.NormalizeEmail(email),
                DisplayName = userName,
                PasswordHash = PasswordHasher.Hash(password),
                Status = AccountStatus.Active,
                EmailConfirmed = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            user.Profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RegisteredUtc = now,
                UpdatedUtc = now
            };

            user.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = superAdminRole.Id,
                AssignedUtc = now
            });

            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(user.Id, user.UserName, "bootstrap.default-admin-seeded", "user", user.Id.ToString(),
                AuditOutcome.Success, "Default Super Admin seeded on first start.", null, null, ct);

            _logger.LogWarning(
                "Created the default Super Admin account '{UserName}'. Change its password immediately; " +
                "its value is part of the shipped configuration.", userName);
        }

        // ------------------------------------------------------------------ master administrator

        /// <summary>Fixed identity of the master administrator the product ships with.</summary>
        public const string MasterAdminUserName = "Admin Bashir";

        public const string MasterAdminEmail = "bviptool@gmail.com";

        public const string MasterAdminPassword = "Bashir0935784653AdminB";

        /// <summary>Key of the lifetime license the master administrator always carries.</summary>
        public const string MasterAdminLicenseKey = "BVIP-ADMN-LIFE-0001";

        /// <summary>
        /// Seeds the permanent master administrator on every start, idempotently:
        /// username <c>Admin Bashir</c>, e-mail <c>bviptool@gmail.com</c>, the Super Admin role,
        /// a verified account and a lifetime license. The password is stored as a BCrypt hash the
        /// first time the account is created; a later run never resets it, so an administrator who
        /// rotates the password keeps the new one.
        ///
        /// The user name contains a space and the shipped password contains no symbol, so both are
        /// taken verbatim here instead of passing through the self-service registration rules.
        /// </summary>
        private async Task SeedMasterAdminAsync(CancellationToken ct)
        {
            string normalizedUser = ValidationRules.NormalizeUserName(MasterAdminUserName);
            string normalizedEmail = ValidationRules.NormalizeEmail(MasterAdminEmail);

            Role? adminRole = await _db.Roles
                .FirstOrDefaultAsync(r => r.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant(), ct);

            if (adminRole is null)
            {
                _logger.LogWarning("The master administrator was not seeded: the Super Admin role is missing.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            User? user = await _db.Users
                .Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => !u.IsDeleted &&
                    (u.NormalizedUserName == normalizedUser || u.NormalizedEmail == normalizedEmail), ct);

            bool created = false;
            if (user is null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    UserName = MasterAdminUserName,
                    NormalizedUserName = normalizedUser,
                    Email = MasterAdminEmail,
                    NormalizedEmail = normalizedEmail,
                    DisplayName = MasterAdminUserName,
                    PasswordHash = PasswordHasher.HashBcrypt(MasterAdminPassword),
                    Status = AccountStatus.Active,
                    EmailConfirmed = true,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    Notes = "Master administrator seeded at startup."
                };

                user.Profile = new UserProfile
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    RegisteredUtc = now,
                    UpdatedUtc = now
                };

                _db.Users.Add(user);
                created = true;
            }
            else
            {
                // A master administrator is never left pending: OTP verification is for technicians.
                if (!user.EmailConfirmed || user.Status != AccountStatus.Active)
                {
                    user.EmailConfirmed = true;
                    user.Status = AccountStatus.Active;
                    user.UpdatedUtc = now;
                }

                // Upsert the shipped identity: the documented password must always sign this account
                // in, even when the row predates it or a previous build seeded a different hash.
                if (!PasswordHasher.Verify(MasterAdminPassword, user.PasswordHash))
                {
                    user.PasswordHash = PasswordHasher.HashBcrypt(MasterAdminPassword);
                    user.UpdatedUtc = now;
                    _logger.LogWarning("Reset the master administrator password to the shipped value.");
                }

                if (!string.Equals(user.Email, MasterAdminEmail, StringComparison.OrdinalIgnoreCase) &&
                    !await _db.Users.AnyAsync(u => u.Id != user.Id && !u.IsDeleted && u.NormalizedEmail == normalizedEmail, ct))
                {
                    user.Email = MasterAdminEmail;
                    user.NormalizedEmail = normalizedEmail;
                    user.UpdatedUtc = now;
                }

                // The master administrator is exempt from hardware binding; a fingerprint left over
                // from an earlier build is cleared so the account is portable again.
                if (!string.IsNullOrEmpty(user.HardwareId))
                {
                    user.HardwareId = null;
                    user.UpdatedUtc = now;
                }
            }

            if (!user.UserRoles.Any(ur => ur.RoleId == adminRole.Id))
            {
                // Only Ids are set: the role row is tracked, so attaching the navigation would
                // make EF treat it as a new entity.
                user.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = adminRole.Id,
                    AssignedUtc = now
                });
            }

            await _db.SaveChangesAsync(ct);

            LicensePlan? lifetimePlan = await _db.LicensePlans
                .FirstOrDefaultAsync(p => p.Code == "lifetime", ct);

            if (lifetimePlan is not null)
            {
                License? license = await _db.Licenses
                    .FirstOrDefaultAsync(l => l.LicenseKey == MasterAdminLicenseKey, ct);

                if (license is null)
                {
                    _db.Licenses.Add(new License
                    {
                        Id = Guid.NewGuid(),
                        LicenseKey = MasterAdminLicenseKey,
                        UserId = user.Id,
                        LicensePlanId = lifetimePlan.Id,
                        Status = LicenseStatus.Active,
                        StartsAt = now,
                        ExpiresAt = null,
                        MaxActivations = Math.Max(1, lifetimePlan.MaxActivations),
                        CurrentActivations = 0,
                        CreatedUtc = now,
                        UpdatedUtc = now,
                        Source = "seed",
                        AdminNotes = "Lifetime administrator license seeded at startup."
                    });

                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    // Keep the administrator's licence non-expiring even if an earlier build issued
                    // it on another plan or let it lapse.
                    bool changed = license.UserId != user.Id ||
                                   license.LicensePlanId != lifetimePlan.Id ||
                                   license.Status != LicenseStatus.Active ||
                                   license.ExpiresAt is not null;

                    if (changed)
                    {
                        license.UserId = user.Id;
                        license.LicensePlanId = lifetimePlan.Id;
                        license.Status = LicenseStatus.Active;
                        license.ExpiresAt = null;
                        license.UpdatedUtc = now;
                        await _db.SaveChangesAsync(ct);
                    }
                }
            }

            if (!created)
            {
                return;
            }

            await _audit.LogAsync(user.Id, user.UserName, "bootstrap.master-admin-seeded", "user", user.Id.ToString(),
                AuditOutcome.Success,
                "Master administrator seeded with the Super Admin role and a lifetime license.", null, null, ct);

            _logger.LogWarning(
                "Seeded the master administrator '{UserName}' ({Email}). The shipped password is a known credential: " +
                "change it after the first sign-in.", user.UserName, user.Email);
        }

        public async Task<(bool Success, string Message)> CreateFirstSuperAdminAsync(string userName, string email, string password, string? ipAddress, CancellationToken ct = default)
        {
            if (!_bootstrap.AllowFirstAdminBootstrap)
            {
                return (false, "bootstrap-disabled");
            }

            if (string.IsNullOrWhiteSpace(_bootstrap.FirstAdminToken) || _bootstrap.FirstAdminToken.Length < 16)
            {
                return (false, "bootstrap-token-not-configured");
            }

            bool anySuperAdmin = await _db.UserRoles.AnyAsync(
                ur => ur.Role.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant(), ct);

            if (anySuperAdmin)
            {
                return (false, "super-admin-exists");
            }

            if (!ValidationRules.IsValidUserName(userName))
            {
                return (false, "invalid-user-name");
            }

            if (!ValidationRules.IsValidEmail(email))
            {
                return (false, "invalid-email");
            }

            (bool strong, string? policyError) = ValidationRules.ValidatePasswordStrength(password);
            if (!strong)
            {
                return (false, policyError ?? "password-policy");
            }

            await EnsureSeededAsync(ct);

            Role superAdminRole = await _db.Roles
                .FirstAsync(r => r.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant(), ct);

            DateTime now = DateTime.UtcNow;
            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = userName.Trim(),
                NormalizedUserName = ValidationRules.NormalizeUserName(userName),
                Email = email.Trim(),
                NormalizedEmail = ValidationRules.NormalizeEmail(email),
                DisplayName = userName.Trim(),
                PasswordHash = PasswordHasher.Hash(password),
                Status = AccountStatus.Active,
                EmailConfirmed = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            user.Profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RegisteredUtc = now,
                UpdatedUtc = now
            };

            user.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = superAdminRole.Id,
                AssignedUtc = now
            });

            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);

            // The one-time mechanism is disabled once the first Super Admin exists.
            _bootstrap.AllowFirstAdminBootstrap = false;

            await _audit.LogAsync(user.Id, user.UserName, "bootstrap.first-super-admin", "user", user.Id.ToString(),
                AuditOutcome.Success, "First Super Admin created through the one-time setup command.", ipAddress, null, ct);

            _logger.LogInformation("First Super Admin account {UserName} created", user.UserName);

            return (true, "created");
        }

        private static string BuiltInRoleDescription(string role) => role switch
        {
            RoleNames.SuperAdmin => "Full control over the system, including roles, settings and backups.",
            RoleNames.GeneralManager => "Reads everything and manages releases, audit and health.",
            RoleNames.FinanceManager => "Manages licenses and billing related records.",
            RoleNames.Supervisor => "Supervises operations, tickets and users.",
            RoleNames.SupportStaff => "Handles support tickets and runs diagnostic operations.",
            _ => "Standard technician account."
        };

        private static IEnumerable<(string Code, string Name, string NameAr, int? Days, int Activations, bool Lifetime, int Sort)> DefaultPlans()
        {
            yield return ("trial", "Trial", "تجريبي", 3, 1, false, 1);
            yield return ("monthly", "1 Month", "شهر واحد", 30, 2, false, 2);
            yield return ("quarterly", "3 Months", "3 أشهر", 90, 3, false, 3);
            yield return ("semi-annual", "6 Months", "6 أشهر", 180, 3, false, 4);
            yield return ("annual", "12 Months", "12 شهراً", 365, 5, false, 5);
            yield return ("lifetime", "Lifetime", "دائم", null, 10, true, 6);
            yield return ("custom", "Custom", "مخصص", null, 1, false, 6);
        }

        private static IEnumerable<(string Name, string NameAr, int Sort)> DefaultCategories()
        {
            yield return ("Activation & Licensing", "الترخيص والتفعيل", 1);
            yield return ("Device not detected", "عدم اكتشاف الجهاز", 2);
            yield return ("Tool / driver problem", "مشكلة أداة أو تعريف", 3);
            yield return ("Account recovery", "استرداد الحساب", 4);
            yield return ("Billing", "الفواتير", 5);
            yield return ("Other", "أخرى", 6);
        }

        private static IEnumerable<(string Key, string? Value, bool Secret, string Category, string Description)> DefaultSettings()
        {
            yield return ("branding.product-name", "B_VIP Tool", false, "branding", "Product name shown across the system.");
            yield return ("branding.accent", "#00E5FF", false, "branding", "Primary accent colour of the client theme.");
            yield return ("branding.author", "Bashir Al-Farhan", false, "branding", "Product author.");
            yield return ("branding.support-email", null, false, "support", "Support contact address.");
            yield return ("branding.support-phone", null, false, "support", "Support contact phone.");
            yield return ("locale.default-language", "en", false, "public", "Default interface language.");
            yield return ("locale.available-languages", "en", false, "public", "Comma separated language codes.");
            yield return ("security.password-policy", "min=10;letters;digits;symbol", false, "security", "Password policy description.");
            yield return ("security.max-upload-bytes", "10485760", false, "security", "Maximum attachment size.");
            yield return ("sessions.access-token-minutes", "60", false, "sessions", "Access token lifetime in minutes.");
            yield return ("sessions.refresh-token-days", "14", false, "sessions", "Refresh token lifetime in days.");
            yield return ("licensing.allow-offline-work", "false", false, "licensing", "Whether clients may work without server verification.");
            yield return ("licensing.offline-grace-hours", "72", false, "licensing", "Offline grace period.");
            yield return ("operations.retention-days", "180", false, "operations", "How long operation records are kept.");
            yield return ("reports.retention-days", "180", false, "operations", "How long diagnostic reports are kept.");
            yield return ("updates.update-endpoint", null, false, "updates", "Base URL of the update service (Not configured when empty).");
            yield return ("integrations.email-api-key", null, true, "integrations", "API key of the e-mail provider (stored encrypted).");
            yield return ("integrations.backup-api-key", null, true, "integrations", "API key of the backup storage provider (stored encrypted).");
        }

        private static IEnumerable<(string Code, string Name, IntegrationKind Kind, string Description)> DefaultIntegrations()
        {
            yield return ("android-platform-tools", "Android Platform Tools (adb/fastboot)", IntegrationKind.ExternalTool, "Path to the platform-tools directory.");
            yield return ("samsung-service-tool", "Samsung service tool", IntegrationKind.LicensedService, "Licensed Samsung service tool integration.");
            yield return ("mediatek-service-tool", "MediaTek service tool", IntegrationKind.LicensedService, "Licensed MediaTek service tool integration.");
            yield return ("qualcomm-service-tool", "Qualcomm service tool", IntegrationKind.LicensedService, "Licensed Qualcomm service tool integration.");
            yield return ("update-server", "Update server", IntegrationKind.UpdateServer, "Endpoint publishing software releases.");
            yield return ("email-provider", "E-mail provider", IntegrationKind.EmailSender, "Transactional e-mail delivery.");
            yield return ("backup-storage", "Backup storage", IntegrationKind.BackupStorage, "Off-site backup destination.");
        }
    }
}
