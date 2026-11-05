using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Users;
using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.Core.Entities;
using B_VIP.Core.Rules;
using B_VIP.Core.Security;
using B_VIP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>User directory, roles and permission administration. All mutations are audited.</summary>
    public sealed class UserAdminService : IUserAdminService
    {
        private readonly B_VIPDbContext _db;
        private readonly IAuditLogger _audit;
        private readonly ILogger<UserAdminService> _logger;

        public UserAdminService(B_VIPDbContext db, IAuditLogger audit, ILogger<UserAdminService> logger)
        {
            _db = db;
            _audit = audit;
            _logger = logger;
        }

        public async Task<PagedResult<UserSummaryDto>> SearchUsersAsync(string? query, string? status, string? role, int page, int pageSize, CancellationToken ct = default)
        {
            page = Paging.NormalizePage(page);
            pageSize = Paging.NormalizePageSize(pageSize);

            IQueryable<User> users = _db.Users.AsNoTracking().Where(u => !u.IsDeleted);

            if (!string.IsNullOrWhiteSpace(query))
            {
                string term = query.Trim().ToUpperInvariant();
                users = users.Where(u =>
                    u.NormalizedUserName.Contains(term) ||
                    u.NormalizedEmail.Contains(term) ||
                    u.DisplayName.ToUpper().Contains(term));
            }

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<AccountStatus>(status, true, out AccountStatus parsed))
            {
                users = users.Where(u => u.Status == parsed);
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                string normalizedRole = role.Trim().ToUpperInvariant();
                users = users.Where(u => u.UserRoles.Any(ur => ur.Role.NormalizedName == normalizedRole));
            }

            int total = await users.CountAsync(ct);

            List<User> items = await users
                .OrderBy(u => u.DisplayName)
                .ThenBy(u => u.UserName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ToListAsync(ct);

            return new PagedResult<UserSummaryDto>(
                items.Select(u => AuthService.MapSummary(u, u.UserRoles.Select(ur => ur.Role.Name).ToList())).ToList(),
                total, page, pageSize);
        }

        public async Task<UserProfileDto?> GetUserAsync(Guid id, CancellationToken ct = default)
        {
            User? user = await _db.Users.AsNoTracking()
                .Include(u => u.Profile)
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .Include(u => u.Licenses).ThenInclude(l => l.LicensePlan)
                .FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);

            if (user is null) return null;

            int openTickets = await _db.ServiceTickets.CountAsync(t => t.UserId == id && t.Status != TicketStatus.Closed, ct);
            int totalOperations = await _db.DeviceServiceOperations.CountAsync(o => o.UserId == id, ct);

            return new UserProfileDto(
                user.Id,
                user.UserName,
                user.DisplayName,
                user.Email,
                user.Status.ToString(),
                user.Profile?.Phone,
                user.Profile?.Company,
                user.Profile?.Country,
                user.Profile?.PreferredLanguage,
                user.UserRoles.Select(ur => ur.Role.Name).ToList(),
                user.CreatedUtc,
                user.LastLoginUtc,
                user.Notes,
                user.Licenses.Select(l => new UserLicenseDto(
                    l.Id, l.LicenseKey, l.LicensePlan.Name, l.Status.ToString(),
                    l.StartsAt, l.ExpiresAt, l.MaxActivations, l.CurrentActivations)).ToList(),
                openTickets,
                totalOperations,
                user.EmailConfirmed,
                user.HardwareId,
                user.HardwareIdResetCount);
        }

        public async Task<UserSummaryDto> CreateUserAsync(CreateUserRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            if (!ValidationRules.IsValidUserName(request?.UserName))
            {
                throw new DomainException("invalid-user-name", "User name must be 3-64 letters, digits, '.', '_' or '-'.");
            }

            if (!ValidationRules.IsValidEmail(request?.Email))
            {
                throw new DomainException("invalid-email", "A valid e-mail address is required.");
            }

            (bool strong, string? policyError) = ValidationRules.ValidatePasswordStrength(request!.Password);
            if (!strong)
            {
                throw new DomainException(policyError!, "The password does not meet the policy.");
            }

            string normalizedUserName = ValidationRules.NormalizeUserName(request.UserName);
            string normalizedEmail = ValidationRules.NormalizeEmail(request.Email);

            bool exists = await _db.Users.AnyAsync(u =>
                u.NormalizedUserName == normalizedUserName || u.NormalizedEmail == normalizedEmail, ct);
            if (exists)
            {
                throw new DomainException("user-exists", "A user with that name or e-mail already exists.", 409);
            }

            DateTime now = DateTime.UtcNow;
            User user = new()
            {
                Id = Guid.NewGuid(),
                UserName = request.UserName.Trim(),
                NormalizedUserName = normalizedUserName,
                Email = request.Email.Trim(),
                NormalizedEmail = normalizedEmail,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.UserName : request.DisplayName.Trim(),
                PasswordHash = PasswordHasher.Hash(request.Password),
                Status = AccountStatus.Active,
                // Accounts created by an administrator are trusted and skip e-mail verification.
                EmailConfirmed = true,
                CreatedUtc = now,
                UpdatedUtc = now,
                CreatedByUserId = actorUserId.ToString()
            };

            user.Profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RegisteredUtc = now,
                UpdatedUtc = now
            };

            IReadOnlyList<string> requestedRoles = request.Roles ?? new[] { RoleNames.User };
            List<Role> roles = await _db.Roles
                .Where(r => requestedRoles.Contains(r.Name))
                .ToListAsync(ct);

            if (roles.Count == 0)
            {
                Role? fallback = await _db.Roles.FirstOrDefaultAsync(r => r.NormalizedName == RoleNames.User.ToUpperInvariant(), ct);
                if (fallback is not null) roles.Add(fallback);
            }

            foreach (Role role in roles)
            {
                user.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = role.Id,
                    AssignedUtc = now,
                    AssignedByUserId = actorUserId
                });
            }

            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.create", "user", user.Id.ToString(),
                AuditOutcome.Success, $"Created user {user.UserName} with roles [{string.Join(", ", roles.Select(r => r.Name))}].",
                null, correlationId, ct);

            _logger.LogInformation("Created user {UserName} ({UserId})", user.UserName, user.Id);

            return AuthService.MapSummary(user, roles.Select(r => r.Name).ToList());
        }

        public async Task<UserProfileDto?> UpdateUserAsync(Guid id, AdminUpdateUserRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User? user = await _db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (user is null) return null;

            if (!string.IsNullOrWhiteSpace(request?.DisplayName)) user.DisplayName = request.DisplayName.Trim();

            if (!string.IsNullOrWhiteSpace(request?.Email))
            {
                if (!ValidationRules.IsValidEmail(request.Email))
                {
                    throw new DomainException("invalid-email", "A valid e-mail address is required.");
                }

                string normalizedEmail = ValidationRules.NormalizeEmail(request.Email);
                bool taken = await _db.Users.AnyAsync(u => u.Id != id && u.NormalizedEmail == normalizedEmail, ct);
                if (taken) throw new DomainException("email-taken", "That e-mail address is already in use.", 409);

                user.Email = request.Email.Trim();
                user.NormalizedEmail = normalizedEmail;
            }

            if (!string.IsNullOrWhiteSpace(request?.Notes)) user.Notes = request.Notes.Trim();

            if (!string.IsNullOrWhiteSpace(request?.Status) && Enum.TryParse<AccountStatus>(request.Status, true, out AccountStatus status))
            {
                if (status == AccountStatus.Suspended)
                {
                    await EnsureNotLastSuperAdminAsync(user, ct);
                }

                user.Status = status;
            }

            user.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.update", "user", user.Id.ToString(),
                AuditOutcome.Success, $"Updated profile; status={user.Status}.", null, correlationId, ct);

            return await GetUserAsync(id, ct);
        }

        public async Task<bool> SuspendUserAsync(Guid id, string reason, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (user is null) return false;
            if (user.Status == AccountStatus.Suspended) return true;

            await EnsureNotLastSuperAdminAsync(user, ct);

            user.Status = AccountStatus.Suspended;
            user.Notes = string.IsNullOrWhiteSpace(reason) ? user.Notes : $"[{DateTime.UtcNow:u}] Suspended: {reason}";
            user.UpdatedUtc = DateTime.UtcNow;

            DateTime now = DateTime.UtcNow;
            foreach (UserSession session in await _db.UserSessions.Where(s => s.UserId == id && s.RevokedUtc == null).ToListAsync(ct))
            {
                session.RevokedUtc = now;
            }

            foreach (RefreshToken token in await _db.RefreshTokens.Where(t => t.UserId == id && t.RevokedUtc == null).ToListAsync(ct))
            {
                token.RevokedUtc = now;
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.suspend", "user", id.ToString(),
                AuditOutcome.Success, Truncate(reason, 500), null, correlationId, ct);

            return true;
        }

        public async Task<bool> ReactivateUserAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (user is null) return false;

            user.Status = AccountStatus.Active;
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            user.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.reactivate", "user", id.ToString(),
                AuditOutcome.Success, null, null, correlationId, ct);
            return true;
        }

        public async Task<int> RevokeSessionsAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;
            List<UserSession> sessions = await _db.UserSessions.Where(s => s.UserId == id && s.RevokedUtc == null).ToListAsync(ct);
            List<RefreshToken> tokens = await _db.RefreshTokens.Where(t => t.UserId == id && t.RevokedUtc == null).ToListAsync(ct);

            foreach (UserSession session in sessions) session.RevokedUtc = now;
            foreach (RefreshToken token in tokens) token.RevokedUtc = now;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.revoke-sessions", "user", id.ToString(),
                AuditOutcome.Success, $"Revoked {sessions.Count} sessions.", null, correlationId, ct);

            return sessions.Count;
        }

        public async Task<IReadOnlyList<string>> AssignRolesAsync(Guid id, IReadOnlyList<string> roles, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User? user = await _db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
                ?? throw new DomainException("user-not-found", "User not found.", 404);

            (IReadOnlyList<string> actorRoles, IReadOnlyList<string> actorPermissions) =
                await AuthorizationResolver.ResolveAsync(_db, actorUserId, ct);

            var requested = (roles ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
            var validRoles = await _db.Roles.Where(r => requested.Contains(r.Name)).Select(r => r.Name).ToListAsync(ct);

            if (!PermissionRules.CanAssignRole(actorRoles.FirstOrDefault() ?? string.Empty, actorPermissions, validRoles))
            {
                await _audit.LogAsync(actorUserId, null, "user.assign-roles", "user", id.ToString(),
                    AuditOutcome.Denied, "Privilege escalation blocked.", null, correlationId, ct);
                throw new DomainException("privilege-escalation", "You cannot grant a role or permission you do not hold yourself.", 403);
            }

            // The last Super Admin must keep the role.
            bool targetIsLastSuperAdmin = user.UserRoles.Any(ur => ur.Role.Name == RoleNames.SuperAdmin);
            if (targetIsLastSuperAdmin && !validRoles.Contains(RoleNames.SuperAdmin))
            {
                int superAdmins = await CountSuperAdminsAsync(ct);
                if (superAdmins <= 1)
                {
                    throw new DomainException("last-super-admin", "The last Super Admin account cannot lose its role.", 409);
                }
            }

            List<Role> roleEntities = await _db.Roles.Where(r => requested.Contains(r.Name)).ToListAsync(ct);
            DateTime now = DateTime.UtcNow;
            var removed = user.UserRoles.Select(ur => ur.Role.Name).Except(roleEntities.Select(r => r.Name)).ToList();
            var added = roleEntities.Select(r => r.Name).Except(user.UserRoles.Select(ur => ur.Role.Name)).ToList();

            foreach (UserRole existing in user.UserRoles.Where(ur => removed.Contains(ur.Role.Name)).ToList())
            {
                user.UserRoles.Remove(existing);
                _db.UserRoles.Remove(existing);
            }

            foreach (Role role in roleEntities)
            {
                if (user.UserRoles.All(ur => ur.RoleId != role.Id))
                {
                    user.UserRoles.Add(new UserRole
                    {
                        UserId = user.Id,
                        RoleId = role.Id,
                        AssignedUtc = now,
                        AssignedByUserId = actorUserId
                    });
                }
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.assign-roles", "user", id.ToString(),
                AuditOutcome.Success,
                $"Added [{string.Join(", ", added)}], removed [{string.Join(", ", removed)}].",
                null, correlationId, ct);

            return roleEntities.Select(r => r.Name).ToList();
        }

        public async Task<IReadOnlyList<SessionDto>> GetSessionsAsync(Guid id, CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;
            List<UserSession> sessions = await _db.UserSessions.AsNoTracking()
                .Where(s => s.UserId == id)
                .OrderByDescending(s => s.CreatedUtc)
                .Take(100)
                .ToListAsync(ct);

            return sessions.Select(s => new SessionDto(
                s.Id, s.CreatedUtc, s.ExpiresUtc, s.LastSeenUtc, s.RevokedUtc,
                s.IpAddress, s.UserAgent, s.IsActive(now))).ToList();
        }

        public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default)
        {
            List<Role> roles = await _db.Roles.AsNoTracking().Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
                .OrderBy(r => r.Name)
                .ToListAsync(ct);

            var userCounts = await _db.UserRoles.GroupBy(ur => ur.RoleId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            return roles.Select(r => new RoleDto(
                r.Id,
                r.Name,
                r.Description,
                r.IsSystem,
                userCounts.TryGetValue(r.Id, out int count) ? count : 0,
                r.RolePermissions.Select(rp => rp.Permission.Name).OrderBy(n => n).ToList(),
                r.CreatedUtc)).ToList();
        }

        public async Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.Name))
            {
                throw new DomainException("invalid-role", "Role name is required.");
            }

            string normalized = request.Name.Trim().ToUpperInvariant();
            if (await _db.Roles.AnyAsync(r => r.NormalizedName == normalized, ct))
            {
                throw new DomainException("role-exists", "A role with that name already exists.", 409);
            }

            List<Permission> permissions = await _db.Permissions
                .Where(p => (request.Permissions ?? Array.Empty<string>()).Contains(p.Name))
                .ToListAsync(ct);

            DateTime now = DateTime.UtcNow;
            Role role = new()
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                NormalizedName = normalized,
                Description = request.Description,
                IsSystem = false,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            foreach (Permission permission in permissions)
            {
                role.RolePermissions.Add(new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = permission.Id,
                    GrantedUtc = now
                });
            }

            _db.Roles.Add(role);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "role.create", "role", role.Id.ToString(),
                AuditOutcome.Success, $"{role.Name} with {permissions.Count} permissions.", null, correlationId, ct);

            return new RoleDto(role.Id, role.Name, role.Description, role.IsSystem, 0,
                permissions.Select(p => p.Name).ToList(), role.CreatedUtc);
        }

        public async Task<RoleDto?> UpdateRoleAsync(Guid id, UpdateRoleRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            Role? role = await _db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return null;

            if (!string.IsNullOrWhiteSpace(request?.Name))
            {
                string normalized = request.Name.Trim().ToUpperInvariant();
                if (await _db.Roles.AnyAsync(r => r.Id != id && r.NormalizedName == normalized, ct))
                {
                    throw new DomainException("role-exists", "A role with that name already exists.", 409);
                }

                role.Name = request.Name.Trim();
                role.NormalizedName = normalized;
            }

            if (request?.Description is not null) role.Description = request.Description;
            role.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "role.update", "role", role.Id.ToString(),
                AuditOutcome.Success, null, null, correlationId, ct);

            int userCount = await _db.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
            return new RoleDto(role.Id, role.Name, role.Description, role.IsSystem, userCount,
                role.RolePermissions.Select(rp => rp.Permission.Name).OrderBy(n => n).ToList(), role.CreatedUtc);
        }

        public async Task<bool> SetRolePermissionsAsync(Guid id, IReadOnlyList<string> permissions, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            Role? role = await _db.Roles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return false;

            (IReadOnlyList<string> actorRoles, IReadOnlyList<string> actorPermissions) =
                await AuthorizationResolver.ResolveAsync(_db, actorUserId, ct);

            if (!PermissionRules.CanAssignRole(actorRoles.FirstOrDefault() ?? string.Empty, actorPermissions, permissions))
            {
                await _audit.LogAsync(actorUserId, null, "role.assign-permissions", "role", id.ToString(),
                    AuditOutcome.Denied, "Privilege escalation blocked.", null, correlationId, ct);
                throw new DomainException("privilege-escalation", "You cannot grant a permission you do not hold yourself.", 403);
            }

            List<Permission> targets = await _db.Permissions
                .Where(p => permissions.Contains(p.Name))
                .ToListAsync(ct);

            var current = role.RolePermissions.ToList();
            foreach (RolePermission existing in current.Where(rc => targets.All(t => t.Id != rc.PermissionId)))
            {
                role.RolePermissions.Remove(existing);
                _db.RolePermissions.Remove(existing);
            }

            foreach (Permission permission in targets.Where(p => current.All(c => c.PermissionId != p.Id)))
            {
                role.RolePermissions.Add(new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = permission.Id,
                    GrantedUtc = DateTime.UtcNow
                });
            }

            role.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "role.assign-permissions", "role", id.ToString(),
                AuditOutcome.Success, $"{targets.Count} permissions granted.", null, correlationId, ct);
            return true;
        }

        public async Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken ct = default)
        {
            return await _db.Permissions.AsNoTracking()
                .OrderBy(p => p.Group).ThenBy(p => p.Name)
                .Select(p => new PermissionDto(p.Name, p.Group, p.Description))
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<string>> GetPermissionsForUserAsync(Guid userId, CancellationToken ct = default)
        {
            return await (
                from ur in _db.UserRoles
                where ur.UserId == userId
                join rp in _db.RolePermissions on ur.RoleId equals rp.RoleId
                join p in _db.Permissions on rp.PermissionId equals p.Id
                select p.Name
            ).Distinct().ToListAsync(ct);
        }

        public async Task<TechnicianMetricsDto> GetTechnicianMetricsAsync(CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;

            int total = await _db.Users.CountAsync(u => !u.IsDeleted, ct);
            int pending = await _db.Users.CountAsync(u => !u.IsDeleted && !u.EmailConfirmed, ct);
            int suspended = await _db.Users.CountAsync(u => !u.IsDeleted && u.Status == AccountStatus.Suspended, ct);
            int bound = await _db.Users.CountAsync(u => !u.IsDeleted && u.HardwareId != null && u.HardwareId != "", ct);

            int activeSubscriptions = await _db.Licenses.CountAsync(l =>
                l.Status == LicenseStatus.Active && (l.ExpiresAt == null || l.ExpiresAt > now), ct);

            int operations = await _db.DeviceServiceOperations.CountAsync(ct);

            return new TechnicianMetricsDto(total, activeSubscriptions, pending, suspended, bound, operations, now);
        }

        public async Task<ResetHardwareIdResponse> ResetHardwareIdAsync(
            Guid userId, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
                ?? throw new DomainException("user-not-found", "The technician account was not found.", 404);

            string? previous = user.HardwareId;

            user.HardwareId = null;
            user.HardwareIdResetCount += 1;
            user.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.hwid-reset", "user", user.Id.ToString(),
                AuditOutcome.Success,
                $"Hardware ID decoupled for {user.UserName} (was {previous ?? "unbound"}); reset #{user.HardwareIdResetCount}.",
                null, correlationId, ct);

            _logger.LogInformation("Hardware ID for {UserName} was reset by {Actor}", user.UserName, actorUserId);

            return new ResetHardwareIdResponse(user.Id, user.UserName, user.HardwareId, user.HardwareIdResetCount,
                $"{user.UserName} is decoupled. The next sign-in binds the new workstation automatically.");
        }

        public async Task<VerifyEmailResponseDto> SetEmailVerifiedAsync(
            Guid userId, bool verified, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
                ?? throw new DomainException("user-not-found", "The technician account was not found.", 404);

            user.EmailConfirmed = verified;

            if (verified)
            {
                if (user.Status == AccountStatus.PendingVerification)
                {
                    user.Status = AccountStatus.Active;
                }

                user.EmailVerificationTokenHash = null;
                user.EmailVerificationSentUtc = null;
            }
            else if (user.Status == AccountStatus.Active)
            {
                user.Status = AccountStatus.PendingVerification;
            }

            user.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, verified ? "user.email-verified-manual" : "user.email-unverified",
                "user", user.Id.ToString(), AuditOutcome.Success,
                $"E-mail verification for {user.UserName} set to {verified} by an administrator.",
                null, correlationId, ct);

            return new VerifyEmailResponseDto(user.Id, user.UserName, user.EmailConfirmed,
                verified ? "The e-mail address is marked as verified." : "The account is pending verification again.");
        }

        public async Task<bool> DeleteTechnicianAsync(
            Guid userId, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
                ?? throw new DomainException("user-not-found", "The technician account was not found.", 404);

            if (userId == actorUserId)
            {
                throw new DomainException("self-delete", "An administrator cannot remove their own account.", 409);
            }

            await EnsureNotLastSuperAdminAsync(user, ct);

            DateTime now = DateTime.UtcNow;
            user.IsDeleted = true;
            user.Status = AccountStatus.Deleted;
            user.HardwareId = null;
            user.EmailVerificationTokenHash = null;
            user.UpdatedUtc = now;

            foreach (UserSession session in await _db.UserSessions.Where(s => s.UserId == userId && s.RevokedUtc == null).ToListAsync(ct))
            {
                session.RevokedUtc = now;
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "user.delete", "user", user.Id.ToString(),
                AuditOutcome.Success, $"Technician {user.UserName} removed from the directory.", null, correlationId, ct);

            return true;
        }

        private async Task EnsureNotLastSuperAdminAsync(User user, CancellationToken ct)
        {
            bool isSuperAdmin = await _db.UserRoles
                .AnyAsync(ur => ur.UserId == user.Id && ur.Role.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant(), ct);

            if (!isSuperAdmin) return;

            int count = await CountSuperAdminsAsync(ct);
            if (count <= 1)
            {
                throw new DomainException("last-super-admin", "The last Super Admin account cannot be suspended.", 409);
            }
        }

        private Task<int> CountSuperAdminsAsync(CancellationToken ct) =>
            _db.UserRoles.CountAsync(ur =>
                ur.Role.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant() &&
                !ur.User.IsDeleted &&
                ur.User.Status == AccountStatus.Active, ct);

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }
}
