using B_VIP.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Common;
using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.Core.Modules;

namespace B_VIP.Backend.Endpoints
{
    public static class MetaEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPMetaEndpoints(this IEndpointRouteBuilder endpoints)
        {
            // Public product identity: no user data, no configuration secrets.
            endpoints.MapGet("/api/meta", ([FromServices] IConfiguration configuration) =>
            {
                string version = typeof(MetaEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0";

                var payload = new
                {
                    product = "B_VIP Tool",
                    author = "Bashir Al-Farhan",
                    version,
                    serverTimeUtc = DateTime.UtcNow,
                    // English-only product: Arabic is no longer offered as an interface language.
                    languages = new[]
                    {
                        new LanguageDto("en", "English", "English", IsRtl: false)
                    },
                    defaultLanguage = "en",
                    modules = ModuleCatalog.All.Select(m => new
                    {
                        m.Key,
                        title = m.TitleEn,
                        titleAr = m.TitleAr,
                        m.Group,
                        m.RequiresDevice,
                        m.RequiresExternalTool
                    }),
                    endpoints = new
                    {
                        operations = new[]
                        {
                            "/api/device-operations",
                            "/api/device-operations/{id}/result",
                            "/api/device-operations/{id}/cancel"
                        },
                        reports = new[] { "/api/diagnostic-reports" }
                    }
                };

                return Results.Ok(payload);
            })
            .WithName("Meta")
            .AllowAnonymous();

            endpoints.MapGet("/api/bootstrap/status", async ([FromServices] IUserAdminService users, [FromServices] IDataSeeder seeder, CancellationToken ct) =>
            {
                bool hasSuperAdmin = false;
                try
                {
                    PagedResult<Contracts.Auth.UserSummaryDto> admins = await users.SearchUsersAsync(null, null, RoleNames.SuperAdmin, 1, 1, ct);
                    hasSuperAdmin = admins.TotalCount > 0;
                }
                catch (Exception)
                {
                    // The database may not be reachable; the bootstrap endpoint will report the error.
                }

                return Results.Ok(new
                {
                    requiresFirstAdmin = !hasSuperAdmin,
                    referenceDataSeeded = true
                });
            })
            .WithName("BootstrapStatus")
            .AllowAnonymous();

            endpoints.MapPost("/api/bootstrap/first-admin", FirstAdminAsync)
                .WithName("CreateFirstSuperAdmin")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            RouteGroupBuilder notifications = endpoints.MapGroup("/api/notifications").WithTags("Notifications");

            notifications.MapGet("/", async (bool unread, [FromServices] INotificationService service, HttpContext http,
                int page = 1, int pageSize = 50, CancellationToken ct = default) =>
            {
                BackendPrincipal principal = http.RequirePrincipal();
                return Results.Ok(await service.SearchAsync(principal.UserId, unread, page, pageSize, ct));
            })
            .WithName("SearchNotifications");

            notifications.MapPost("/{id:guid}/read", async (Guid id, [FromServices] INotificationService service, HttpContext http, CancellationToken ct) =>
            {
                BackendPrincipal principal = http.RequirePrincipal();
                bool ok = await service.MarkReadAsync(id, principal.UserId, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .WithName("MarkNotificationRead");

            notifications.MapPost("/read-all", async ([FromServices] INotificationService service, HttpContext http, CancellationToken ct) =>
            {
                BackendPrincipal principal = http.RequirePrincipal();
                int count = await service.MarkAllReadAsync(principal.UserId, ct);
                return Results.Ok(new { updated = count });
            })
            .WithName("MarkAllNotificationsRead");

            return endpoints;
        }

        private sealed record FirstAdminRequest(
            string Token,
            string UserName,
            string Email,
            string Password);

        private static async Task<IResult> FirstAdminAsync(
            FirstAdminRequest? request,
            [FromServices] IDataSeeder seeder,
            [FromServices] Microsoft.Extensions.Configuration.IConfiguration configuration,
            HttpContext http,
            CancellationToken ct)
        {
            if (request is null)
            {
                return Results.BadRequest(new ApiErrorEnvelope(new ApiError("invalid-request",
                    "The bootstrap request is empty.", http.GetCorrelationId())));
            }

            string expected = configuration["Bootstrap:FirstAdminToken"] ?? string.Empty;
            string provided = request.Token;

            if (string.IsNullOrWhiteSpace(expected) ||
                !Core.Security.TokenFactory.FixedTimeEquals(expected, provided))
            {
                return Results.Unauthorized();
            }

            (bool success, string message) = await seeder.CreateFirstSuperAdminAsync(
                request.UserName, request.Email, request.Password, http.GetIpAddress(), ct);

            if (!success)
            {
                return message switch
                {
                    "super-admin-exists" => Results.Conflict(new ApiErrorEnvelope(new ApiError(
                        "super-admin-exists", "A Super Admin already exists; the one-time bootstrap is closed.", http.GetCorrelationId()))),
                    "bootstrap-disabled" => Results.Forbid(),
                    _ => Results.BadRequest(new ApiErrorEnvelope(new ApiError(message, "The bootstrap request is invalid.", http.GetCorrelationId())))
                };
            }

            return Results.Ok(new { status = "created" });
        }
    }
}
