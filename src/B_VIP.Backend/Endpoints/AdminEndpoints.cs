using B_VIP.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Common;
using B_VIP.Core;

namespace B_VIP.Backend.Endpoints
{
    public static class AdminEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPAdminEndpoints(this IEndpointRouteBuilder endpoints)
        {
            RouteGroupBuilder admin = endpoints.MapGroup("/api/admin").WithTags("Admin");

            admin.MapGet("/overview", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetOverviewAsync(ct)))
                .WithName("AdminOverview")
                .RequirePermission(Permissions.AdminOverview);

            admin.MapGet("/settings", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetSettingsAsync(ct)))
                .WithName("AdminSettings")
                .RequirePermission(Permissions.AdminSettings);

            admin.MapPatch("/settings/{key}", UpdateSettingAsync)
                .WithName("AdminUpdateSetting")
                .RequirePermission(Permissions.AdminSettings);

            admin.MapGet("/integrations", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetIntegrationsAsync(ct)))
                .WithName("AdminIntegrations")
                .RequirePermission(Permissions.AdminIntegrations);

            admin.MapPost("/integrations", CreateIntegrationAsync)
                .WithName("AdminCreateIntegration")
                .RequirePermission(Permissions.AdminIntegrations);

            admin.MapPatch("/integrations/{id:guid}", UpdateIntegrationAsync)
                .WithName("AdminUpdateIntegration")
                .RequirePermission(Permissions.AdminIntegrations);

            admin.MapPost("/integrations/{id:guid}/test", TestIntegrationAsync)
                .WithName("AdminTestIntegration")
                .RequirePermission(Permissions.AdminIntegrations);

            admin.MapGet("/audit-events", GetAuditEventsAsync)
                .WithName("AdminAuditEvents")
                .RequirePermission(Permissions.AdminAudit);

            admin.MapGet("/health", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetHealthAsync(ct)))
                .WithName("AdminHealth")
                .RequirePermission(Permissions.AdminHealth);

            admin.MapGet("/status", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetStatusAsync(ct)))
                .WithName("AdminStatus")
                .RequirePermission(Permissions.AdminHealth);

            admin.MapGet("/versions", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetReleasesAsync(ct)))
                .WithName("AdminVersions")
                .RequirePermission(Permissions.AdminReleases);

            admin.MapPost("/versions", CreateReleaseAsync)
                .WithName("AdminCreateVersion")
                .RequirePermission(Permissions.AdminReleases);

            admin.MapPatch("/versions/{id:guid}", UpdateReleaseAsync)
                .WithName("AdminUpdateVersion")
                .RequirePermission(Permissions.AdminReleases);

            admin.MapGet("/backups", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetBackupsAsync(ct)))
                .WithName("AdminBackups")
                .RequirePermission(Permissions.AdminBackups);

            admin.MapPost("/backups", CreateBackupAsync)
                .WithName("AdminCreateBackup")
                .RequirePermission(Permissions.AdminBackups);

            admin.MapPost("/backups/{id:guid}/restore", RestoreBackupAsync)
                .WithName("AdminRestoreBackup")
                .RequirePermission(Permissions.AdminBackups);

            admin.MapGet("/maintenance", async ([FromServices] IAdminService service, CancellationToken ct) =>
                    Results.Ok(await service.GetMaintenanceAsync(ct)))
                .WithName("AdminMaintenance")
                .RequirePermission(Permissions.AdminSettings);

            admin.MapPatch("/maintenance", UpdateMaintenanceAsync)
                .WithName("AdminUpdateMaintenance")
                .RequirePermission(Permissions.AdminSettings);

            return endpoints;
        }

        private static async Task<IResult> UpdateSettingAsync(string key, UpdateSettingRequest request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            SettingDto? setting = await service.UpdateSettingAsync(key, request.Value, principal.UserId, http.GetCorrelationId(), ct);
            return setting is null ? Results.NotFound() : Results.Ok(setting);
        }

        private static async Task<IResult> CreateIntegrationAsync(CreateIntegrationRequest request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            IntegrationDto created = await service.CreateIntegrationAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Created($"/api/admin/integrations/{created.Id}", created);
        }

        private static async Task<IResult> UpdateIntegrationAsync(Guid id, UpdateIntegrationRequest request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            IntegrationDto? updated = await service.UpdateIntegrationAsync(id, request, principal.UserId, http.GetCorrelationId(), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }

        private static async Task<IResult> TestIntegrationAsync(Guid id, [FromServices] IAdminService service, CancellationToken ct)
            => Results.Ok(await service.TestIntegrationAsync(id, ct));

        private static async Task<IResult> GetAuditEventsAsync(
            string? action, string? outcome, DateTime? from, DateTime? to,
            [FromServices] IAdminService service,
            int page = 1, int pageSize = 50, CancellationToken ct = default)
            => Results.Ok(await service.GetAuditEventsAsync(action, outcome, from, to, page, pageSize, ct));

        private static async Task<IResult> CreateReleaseAsync(CreateReleaseRequest request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            ReleaseDto release = await service.CreateReleaseAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Created($"/api/admin/versions/{release.Id}", release);
        }

        private static async Task<IResult> UpdateReleaseAsync(Guid id, UpdateReleaseRequest request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            ReleaseDto? release = await service.UpdateReleaseAsync(id, request.Enabled, principal.UserId, http.GetCorrelationId(), ct);
            return release is null ? Results.NotFound() : Results.Ok(release);
        }

        private static async Task<IResult> CreateBackupAsync(CreateBackupRequest? request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            BackupActionResponse response = await service.CreateBackupAsync(principal.UserId, request?.Note, http.GetCorrelationId(), ct);
            return response.Status == "Failed"
                ? Results.Json(response, statusCode: StatusCodes.Status500InternalServerError)
                : Results.Accepted($"/api/admin/backups/{response.Id}", response);
        }

        private static async Task<IResult> RestoreBackupAsync(Guid id, RestoreBackupRequest request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            if (request.BackupId != id)
            {
                return Results.BadRequest(new ApiErrorEnvelope(new ApiError("mismatch", "The route id and payload id differ.", http.GetCorrelationId())));
            }

            BackendPrincipal principal = http.RequirePrincipal();
            BackupActionResponse? response = await service.RestoreBackupAsync(id, request.Confirm, principal.UserId, http.GetCorrelationId(), ct);
            if (response is null) return Results.NotFound();

            return response.Status == "Succeeded"
                ? Results.Ok(response)
                : Results.Conflict(response);
        }

        private static async Task<IResult> UpdateMaintenanceAsync(UpdateMaintenanceRequest request, [FromServices] IAdminService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            MaintenanceDto result = await service.UpdateMaintenanceAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Ok(result);
        }

        private sealed record UpdateReleaseRequest(bool Enabled);
    }
}
