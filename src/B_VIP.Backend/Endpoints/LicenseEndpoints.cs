using B_VIP.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Licenses;
using B_VIP.Core;
using B_VIP.Core.Common;

namespace B_VIP.Backend.Endpoints
{
    public static class LicenseEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPLicenseEndpoints(this IEndpointRouteBuilder endpoints)
        {
            RouteGroupBuilder group = endpoints.MapGroup("/api/licenses").WithTags("Licenses");

            group.MapGet("/", SearchAsync)
                .WithName("SearchLicenses")
                .RequirePermission(Permissions.LicensesRead);

            // Public price list: the onboarding screen shows the tiers before an account exists.
            group.MapGet("/plans", async ([FromServices] ILicenseService service, CancellationToken ct) =>
                    Results.Ok(await service.GetPlansAsync(ct)))
                .WithName("GetLicensePlans")
                .AllowAnonymous();

            group.MapPost("/plans", CreatePlanAsync)
                .WithName("CreateLicensePlan")
                .RequirePermission(Permissions.LicensesManage);

            group.MapPatch("/plans/{id:guid}", UpdatePlanAsync)
                .WithName("UpdateLicensePlan")
                .RequirePermission(Permissions.LicensesManage);

            group.MapDelete("/plans/{id:guid}", DeletePlanAsync)
                .WithName("DeleteLicensePlan")
                .RequirePermission(Permissions.LicensesManage);

            group.MapPost("/", CreateAsync)
                .WithName("CreateLicense")
                .RequirePermission(Permissions.LicensesWrite);

            // Verification and activation are used by the desktop client during sign-in and startup.
            group.MapPost("/verify", VerifyAsync)
                .WithName("VerifyLicense");

            group.MapPost("/activate", ActivateAsync)
                .WithName("ActivateLicense");

            group.MapPost("/deactivate", DeactivateAsync)
                .WithName("DeactivateLicense");

            group.MapGet("/{id:guid}", GetAsync)
                .WithName("GetLicense")
                .RequirePermission(Permissions.LicensesRead);

            group.MapPatch("/{id:guid}", UpdateAsync)
                .WithName("UpdateLicense")
                .RequirePermission(Permissions.LicensesWrite);

            group.MapPost("/{id:guid}/suspend", SuspendAsync)
                .WithName("SuspendLicense")
                .RequirePermission(Permissions.LicensesManage);

            group.MapPost("/{id:guid}/reactivate", ReactivateAsync)
                .WithName("ReactivateLicense")
                .RequirePermission(Permissions.LicensesManage);

            group.MapPost("/{id:guid}/revoke", RevokeAsync)
                .WithName("RevokeLicense")
                .RequirePermission(Permissions.LicensesManage);

            group.MapGet("/{id:guid}/activations", GetActivationsAsync)
                .WithName("GetLicenseActivations")
                .RequirePermission(Permissions.LicensesRead);

            group.MapDelete("/{id:guid}/activations/{activationId:guid}", DeactivateActivationAsync)
                .WithName("DeactivateLicenseActivation")
                .RequirePermission(Permissions.LicensesManage);

            group.MapGet("/{id:guid}/verification-events", GetVerificationEventsAsync)
                .WithName("GetLicenseVerificationEvents")
                .RequirePermission(Permissions.LicensesRead);

            return endpoints;
        }

        private static async Task<IResult> SearchAsync(
            string? q, string? status, Guid? userId,
            [FromServices] ILicenseService service,
            HttpContext http,
            int page = 1, int pageSize = 25, CancellationToken ct = default)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            // A user who is not staff only ever sees their own licenses, whatever filter they send.
            Guid? scope = principal.IsSupport ? userId : principal.UserId;
            PagedResult<LicenseDto> result = await service.SearchLicensesAsync(q, status, scope, page, pageSize, ct);
            return Results.Ok(new PagedResponse<LicenseDto>(result.Items, result.TotalCount, result.Page, result.PageSize, result.TotalPages));
        }

        private static async Task<IResult> CreatePlanAsync(
            CreatePlanRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicensePlanDto plan = await service.CreatePlanAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Created($"/api/licenses/plans/{plan.Id}", plan);
        }

        private static async Task<IResult> UpdatePlanAsync(
            Guid id, UpdatePlanRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicensePlanDto? plan = await service.UpdatePlanAsync(id, request, principal.UserId, http.GetCorrelationId(), ct);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        }

        private static async Task<IResult> DeletePlanAsync(
            Guid id, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            bool deleted = await service.DeletePlanAsync(id, principal.UserId, http.GetCorrelationId(), ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }

        private static async Task<IResult> CreateAsync(CreateLicenseRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicenseDto license = await service.CreateLicenseAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Created($"/api/licenses/{license.Id}", license);
        }

        private static async Task<IResult> VerifyAsync(VerifyLicenseRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal? principal = http.GetPrincipal();
            VerifyLicenseResponse response = await service.VerifyAsync(
                request, principal?.UserId, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return Results.Ok(response);
        }

        private static async Task<IResult> ActivateAsync(ActivateLicenseRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal? principal = http.GetPrincipal();
            ActivateLicenseResponse response = await service.ActivateAsync(
                request, principal?.UserId, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return response.Success ? Results.Ok(response) : Results.Conflict(response);
        }

        private static async Task<IResult> DeactivateAsync(DeactivateLicenseRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal? principal = http.GetPrincipal();
            bool ok = await service.DeactivateAsync(request, principal?.UserId, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }

        private static async Task<IResult> GetAsync(Guid id, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            LicenseDto? license = await service.GetLicenseAsync(id, ct);
            if (license is null) return Results.NotFound();

            BackendPrincipal principal = http.RequirePrincipal();
            if (!principal.IsSupport && license.UserId != principal.UserId)
            {
                // Users only see their own licenses unless they hold support permissions.
                return Results.NotFound();
            }

            return Results.Ok(license);
        }

        private static async Task<IResult> UpdateAsync(Guid id, UpdateLicenseRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicenseDto? license = await service.UpdateLicenseAsync(id, request, principal.UserId, http.GetCorrelationId(), ct);
            return license is null ? Results.NotFound() : Results.Ok(license);
        }

        private static async Task<IResult> SuspendAsync(Guid id, LicenseActionRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicenseDto? license = await service.SuspendLicenseAsync(id, request.Reason, principal.UserId, http.GetCorrelationId(), ct);
            return license is null ? Results.NotFound() : Results.Ok(license);
        }

        private static async Task<IResult> ReactivateAsync(Guid id, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicenseDto? license = await service.ReactivateLicenseAsync(id, principal.UserId, http.GetCorrelationId(), ct);
            return license is null ? Results.NotFound() : Results.Ok(license);
        }

        private static async Task<IResult> RevokeAsync(Guid id, LicenseActionRequest request, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicenseDto? license = await service.RevokeLicenseAsync(id, request.Reason, principal.UserId, http.GetCorrelationId(), ct);
            return license is null ? Results.NotFound() : Results.Ok(license);
        }

        private static async Task<IResult> GetActivationsAsync(Guid id, [FromServices] ILicenseService service, CancellationToken ct)
        {
            LicenseActivationListResponse? response = await service.GetActivationsAsync(id, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        }

        private static async Task<IResult> DeactivateActivationAsync(Guid id, Guid activationId, string? reason, [FromServices] ILicenseService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            bool ok = await service.DeactivateActivationAsync(id, activationId, reason, principal.UserId, http.GetCorrelationId(), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }

        private static async Task<IResult> GetVerificationEventsAsync(Guid id, int limit, [FromServices] ILicenseService service, CancellationToken ct)
            => Results.Ok(await service.GetVerificationEventsAsync(id, limit <= 0 ? 50 : limit, ct));
    }
}
