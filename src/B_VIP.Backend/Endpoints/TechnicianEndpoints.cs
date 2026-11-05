using B_VIP.Application.Interfaces;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Licenses;
using B_VIP.Core;
using B_VIP.Core.Common;

namespace B_VIP.Backend.Endpoints
{
    /// <summary>
    /// Technician administration: the registered user directory with its verification and hardware
    /// state, HWID decoupling, manual verification and subscription extension. Every call requires
    /// the matching permission; the server remains the only source of truth.
    /// </summary>
    public static class TechnicianEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPTechnicianEndpoints(this IEndpointRouteBuilder endpoints)
        {
            RouteGroupBuilder group = endpoints.MapGroup("/api/admin/technicians").WithTags("Technicians");

            group.MapGet("/", SearchAsync)
                .WithName("SearchTechnicians")
                .RequirePermission(Permissions.UsersRead);

            group.MapGet("/metrics", GetMetricsAsync)
                .WithName("GetTechnicianMetrics")
                .RequirePermission(Permissions.UsersRead);

            group.MapPost("/{id:guid}/reset-hwid", ResetHardwareIdAsync)
                .WithName("ResetTechnicianHardwareId")
                .RequirePermission(Permissions.UsersWrite);

            group.MapPost("/{id:guid}/verify-email", SetEmailVerifiedAsync)
                .WithName("SetTechnicianEmailVerified")
                .RequirePermission(Permissions.UsersWrite);

            group.MapPost("/{id:guid}/extend-subscription", ExtendSubscriptionAsync)
                .WithName("ExtendTechnicianSubscription")
                .RequirePermission(Permissions.LicensesWrite);

            group.MapPost("/{id:guid}/plan", ChangePlanAsync)
                .WithName("ChangeTechnicianPlan")
                .RequirePermission(Permissions.LicensesWrite);

            group.MapDelete("/{id:guid}", DeleteAsync)
                .WithName("DeleteTechnician")
                .RequirePermission(Permissions.UsersWrite);

            return endpoints;
        }

        private static async Task<IResult> SearchAsync(
            string? q,
            [Microsoft.AspNetCore.Mvc.FromServices] IUserAdminService users,
            int page = 1,
            int pageSize = 100,
            CancellationToken ct = default)
        {
            PagedResult<UserSummaryDto> result = await users.SearchUsersAsync(q, null, null, page, pageSize, ct);
            return Results.Ok(new PagedResponse<UserSummaryDto>(
                result.Items, result.TotalCount, result.Page, result.PageSize, result.TotalPages));
        }

        private static async Task<IResult> GetMetricsAsync(
            [Microsoft.AspNetCore.Mvc.FromServices] IUserAdminService users,
            CancellationToken ct)
            => Results.Ok(await users.GetTechnicianMetricsAsync(ct));

        private static async Task<IResult> ResetHardwareIdAsync(
            Guid id,
            [Microsoft.AspNetCore.Mvc.FromServices] IUserAdminService users,
            HttpContext http,
            CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            ResetHardwareIdResponse response = await users.ResetHardwareIdAsync(id, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Ok(response);
        }

        private static async Task<IResult> SetEmailVerifiedAsync(
            Guid id,
            SetEmailVerifiedRequest request,
            [Microsoft.AspNetCore.Mvc.FromServices] IUserAdminService users,
            HttpContext http,
            CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            VerifyEmailResponseDto response = await users.SetEmailVerifiedAsync(
                id, request.Verified, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Ok(response);
        }

        private static async Task<IResult> DeleteAsync(
            Guid id,
            [Microsoft.AspNetCore.Mvc.FromServices] IUserAdminService users,
            HttpContext http,
            CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            bool deleted = await users.DeleteTechnicianAsync(id, principal.UserId, http.GetCorrelationId(), ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }

        private static async Task<IResult> ChangePlanAsync(
            Guid id,
            ChangePlanRequest request,
            [Microsoft.AspNetCore.Mvc.FromServices] ILicenseService licenses,
            HttpContext http,
            CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            LicenseDto? license = await licenses.ChangePlanAsync(
                id, request.PlanId, request.ResetPeriod, principal.UserId, http.GetCorrelationId(), ct);

            return license is null
                ? Results.NotFound(new ApiErrorEnvelope(new ApiError("license-not-found",
                    "No license exists for this technician yet.", http.GetCorrelationId())))
                : Results.Ok(license);
        }

        private static async Task<IResult> ExtendSubscriptionAsync(
            Guid id,
            ExtendSubscriptionRequest request,
            [Microsoft.AspNetCore.Mvc.FromServices] ILicenseService licenses,
            HttpContext http,
            CancellationToken ct)
        {
            if (request.Days is <= 0 or > 3650)
            {
                return Results.BadRequest(new ApiErrorEnvelope(new ApiError("invalid-duration",
                    "The extension must be between 1 and 3650 days.", http.GetCorrelationId())));
            }

            BackendPrincipal principal = http.RequirePrincipal();

            PagedResult<LicenseDto> existing = await licenses.SearchLicensesAsync(null, null, id, 1, 1, ct);
            LicenseDto? license = existing.Items.FirstOrDefault();

            if (license is null)
            {
                return Results.NotFound(new ApiErrorEnvelope(new ApiError("license-not-found",
                    "This technician has no license yet. Issue one from the license generator first.",
                    http.GetCorrelationId())));
            }

            DateTime basis = license.ExpiresAt.HasValue && license.ExpiresAt.Value > DateTime.UtcNow
                ? license.ExpiresAt.Value
                : DateTime.UtcNow;

            LicenseDto? updated = await licenses.UpdateLicenseAsync(
                license.Id,
                new UpdateLicenseRequest(null, basis.AddDays(request.Days), null, request.Reason ?? license.AdminNotes, null),
                principal.UserId,
                http.GetCorrelationId(),
                ct);

            if (updated is null)
            {
                return Results.NotFound(new ApiErrorEnvelope(new ApiError("license-not-found",
                    "The license could not be updated.", http.GetCorrelationId())));
            }

            return Results.Ok(new ExtendSubscriptionResponse(
                updated.Id,
                updated.LicenseKey,
                updated.PlanName,
                updated.ExpiresAt,
                $"Subscription extended by {request.Days} day(s)."));
        }
    }
}
