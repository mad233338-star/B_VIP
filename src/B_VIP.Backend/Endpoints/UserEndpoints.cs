using B_VIP.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Users;
using B_VIP.Core;
using B_VIP.Core.Common;

namespace B_VIP.Backend.Endpoints
{
    public static class UserEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPUserEndpoints(this IEndpointRouteBuilder endpoints)
        {
            RouteGroupBuilder users = endpoints.MapGroup("/api/users").WithTags("Users");

            users.MapGet("/", SearchAsync)
                .WithName("SearchUsers")
                .RequirePermission(Permissions.UsersRead);

            users.MapGet("/{id:guid}", GetUserAsync)
                .WithName("GetUser")
                .RequirePermission(Permissions.UsersRead);

            users.MapPost("/", CreateUserAsync)
                .WithName("CreateUser")
                .RequirePermission(Permissions.UsersWrite);

            users.MapPatch("/{id:guid}", UpdateUserAsync)
                .WithName("UpdateUser")
                .RequirePermission(Permissions.UsersWrite);

            users.MapPost("/{id:guid}/suspend", SuspendAsync)
                .WithName("SuspendUser")
                .RequirePermission(Permissions.UsersSuspend);

            users.MapPost("/{id:guid}/reactivate", ReactivateAsync)
                .WithName("ReactivateUser")
                .RequirePermission(Permissions.UsersSuspend);

            users.MapPost("/{id:guid}/revoke-sessions", RevokeSessionsAsync)
                .WithName("RevokeUserSessions")
                .RequirePermission(Permissions.UsersWrite);

            users.MapPut("/{id:guid}/roles", AssignRolesAsync)
                .WithName("AssignUserRoles")
                .RequirePermission(Permissions.UsersRolesWrite);

            users.MapGet("/{id:guid}/sessions", GetSessionsAsync)
                .WithName("GetUserSessions")
                .RequirePermission(Permissions.UsersRead);

            RouteGroupBuilder roles = endpoints.MapGroup("/api/admin/roles").WithTags("Roles");

            roles.MapGet("/", GetRolesAsync)
                .WithName("GetRoles")
                .RequirePermission(Permissions.RolesRead);

            roles.MapPost("/", CreateRoleAsync)
                .WithName("CreateRole")
                .RequirePermission(Permissions.RolesWrite);

            roles.MapPatch("/{id:guid}", UpdateRoleAsync)
                .WithName("UpdateRole")
                .RequirePermission(Permissions.RolesWrite);

            roles.MapPut("/{id:guid}/permissions", SetRolePermissionsAsync)
                .WithName("SetRolePermissions")
                .RequirePermission(Permissions.RolesWrite);

            endpoints.MapGet("/api/admin/permissions", async ([FromServices] IUserAdminService users1, CancellationToken ct) =>
                    Results.Ok(await users1.GetPermissionsAsync(ct)))
                .WithName("GetPermissions")
                .RequirePermission(Permissions.RolesRead);

            return endpoints;
        }

        private static async Task<IResult> SearchAsync(
            string? q, string? status, string? role,
            [FromServices] IUserAdminService service,
            int page = 1, int pageSize = 25, CancellationToken ct = default)
        {
            PagedResult<UserSummaryDto> result = await service.SearchUsersAsync(q, status, role, page, pageSize, ct);
            return Results.Ok(new PagedResponse<UserSummaryDto>(result.Items, result.TotalCount, result.Page, result.PageSize, result.TotalPages));
        }

        private static async Task<IResult> GetUserAsync(Guid id, [FromServices] IUserAdminService service, CancellationToken ct)
        {
            UserProfileDto? user = await service.GetUserAsync(id, ct);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }

        private static async Task<IResult> CreateUserAsync(CreateUserRequest request, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            UserSummaryDto created = await service.CreateUserAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Created($"/api/users/{created.Id}", created);
        }

        private static async Task<IResult> UpdateUserAsync(Guid id, AdminUpdateUserRequest request, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            UserProfileDto? updated = await service.UpdateUserAsync(id, request, principal.UserId, http.GetCorrelationId(), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }

        private static async Task<IResult> SuspendAsync(Guid id, SuspendUserRequest request, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            bool ok = await service.SuspendUserAsync(id, request.Reason, principal.UserId, http.GetCorrelationId(), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }

        private static async Task<IResult> ReactivateAsync(Guid id, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            bool ok = await service.ReactivateUserAsync(id, principal.UserId, http.GetCorrelationId(), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }

        private static async Task<IResult> RevokeSessionsAsync(Guid id, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            int revoked = await service.RevokeSessionsAsync(id, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Ok(new { revoked });
        }

        private static async Task<IResult> AssignRolesAsync(Guid id, AssignRolesRequest request, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            IReadOnlyList<string> roles = await service.AssignRolesAsync(id, request.Roles, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Ok(new { roles });
        }

        private static async Task<IResult> GetSessionsAsync(Guid id, [FromServices] IUserAdminService service, CancellationToken ct)
            => Results.Ok(await service.GetSessionsAsync(id, ct));

        private static async Task<IResult> GetRolesAsync([FromServices] IUserAdminService service, CancellationToken ct)
            => Results.Ok(await service.GetRolesAsync(ct));

        private static async Task<IResult> CreateRoleAsync(CreateRoleRequest request, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            RoleDto role = await service.CreateRoleAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Created($"/api/admin/roles/{role.Id}", role);
        }

        private static async Task<IResult> UpdateRoleAsync(Guid id, UpdateRoleRequest request, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            RoleDto? role = await service.UpdateRoleAsync(id, request, principal.UserId, http.GetCorrelationId(), ct);
            return role is null ? Results.NotFound() : Results.Ok(role);
        }

        private static async Task<IResult> SetRolePermissionsAsync(Guid id, SetRolePermissionsRequest request, [FromServices] IUserAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            bool ok = await service.SetRolePermissionsAsync(id, request.Permissions, principal.UserId, http.GetCorrelationId(), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }
    }
}
