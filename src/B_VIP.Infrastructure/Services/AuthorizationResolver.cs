using B_VIP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>
    /// Single source of truth for resolving a user's effective roles and permissions from the database.
    /// Used by authentication, authorization and administration alike.
    /// </summary>
    public static class AuthorizationResolver
    {
        public static async Task<(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions)> ResolveAsync(
            B_VIPDbContext db,
            Guid userId,
            CancellationToken ct = default)
        {
            List<string> roles = await (
                from ur in db.UserRoles
                where ur.UserId == userId
                join r in db.Roles on ur.RoleId equals r.Id
                select r.Name
            ).ToListAsync(ct);

            List<string> permissions = await (
                from ur in db.UserRoles
                where ur.UserId == userId
                join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                join p in db.Permissions on rp.PermissionId equals p.Id
                select p.Name
            ).Distinct().ToListAsync(ct);

            return (roles, permissions);
        }
    }
}
