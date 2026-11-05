using B_VIP.Application.Interfaces;

namespace B_VIP.Backend.Auth
{
    /// <summary>The authenticated principal attached to the current HTTP context.</summary>
    public sealed record BackendPrincipal(
        Guid UserId,
        Guid? SessionId,
        string UserName,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions,
        string? IpAddress,
        string? CorrelationId)
    {
        public bool HasPermission(string permission) =>
            Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

        public bool HasAnyPermission(params string[] required) =>
            required.Any(HasPermission);

        public bool IsSupport =>
            Roles.Contains(B_VIP.Core.RoleNames.SupportStaff, StringComparer.OrdinalIgnoreCase) ||
            Roles.Contains(B_VIP.Core.RoleNames.Supervisor, StringComparer.OrdinalIgnoreCase) ||
            Roles.Contains(B_VIP.Core.RoleNames.GeneralManager, StringComparer.OrdinalIgnoreCase) ||
            Roles.Contains(B_VIP.Core.RoleNames.SuperAdmin, StringComparer.OrdinalIgnoreCase);

        public bool IsSuperAdmin =>
            Roles.Contains(B_VIP.Core.RoleNames.SuperAdmin, StringComparer.OrdinalIgnoreCase);
    }

    public static class HttpContextPrincipalExtensions
    {
        private const string Key = "B_VIP.Principal";

        public static void SetPrincipal(this HttpContext context, BackendPrincipal principal)
            => context.Items[Key] = principal;

        public static BackendPrincipal? GetPrincipal(this HttpContext context)
            => context.Items[Key] as BackendPrincipal;

        /// <summary>Returns the principal or throws an unauthorized result for minimal API handlers.</summary>
        public static BackendPrincipal RequirePrincipal(this HttpContext context)
            => context.GetPrincipal()
               ?? throw new B_VIP.Core.Common.DomainException("unauthorized", "Authentication is required.", 401);

        public static string? GetIpAddress(this HttpContext context)
            => context.Connection.RemoteIpAddress?.ToString();

        public static string? GetCorrelationId(this HttpContext context)
            => context.Items["B_VIP.CorrelationId"] as string;
    }
}
