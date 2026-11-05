using B_VIP.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Reports;
using B_VIP.Core;

namespace B_VIP.Backend.Endpoints
{
    public static class OperationEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPOperationEndpoints(this IEndpointRouteBuilder endpoints)
        {
            RouteGroupBuilder operations = endpoints.MapGroup("/api/device-operations").WithTags("Operations");

            operations.MapGet("/", SearchAsync)
                .WithName("SearchOperations")
                .RequirePermission(Permissions.OperationsRead);

            operations.MapPost("/", CreateAsync)
                .WithName("CreateOperation")
                .RequirePermission(Permissions.OperationsRun);

            operations.MapGet("/{id:guid}", GetAsync)
                .WithName("GetOperation")
                .RequirePermission(Permissions.OperationsRead);

            operations.MapGet("/{id:guid}/events", GetEventsAsync)
                .WithName("GetOperationEvents")
                .RequirePermission(Permissions.OperationsRead);

            // Owners may always cancel their own operation; the permission widens that to any user's.
            operations.MapPost("/{id:guid}/cancel", CancelAsync)
                .WithName("CancelOperation")
                .RequirePermission(Permissions.OperationsRead);

            // Extra endpoint required to record the real outcome of a locally executed tool run.
            operations.MapPost("/{id:guid}/result", RecordResultAsync)
                .WithName("RecordOperationResult")
                .RequirePermission(Permissions.OperationsRun);

            RouteGroupBuilder reports = endpoints.MapGroup("/api/diagnostic-reports").WithTags("Diagnostic reports");

            reports.MapGet("/", SearchReportsAsync)
                .WithName("SearchReports")
                .RequirePermission(Permissions.ReportsRead);

            reports.MapPost("/", CreateReportAsync)
                .WithName("CreateReport")
                .RequirePermission(Permissions.ReportsWrite);

            reports.MapGet("/{id:guid}", GetReportAsync)
                .WithName("GetReport")
                .RequirePermission(Permissions.ReportsRead);

            return endpoints;
        }

        private static async Task<IResult> SearchAsync(
            string? status, string? type, Guid? userId,
            [FromServices] IOperationService service,
            HttpContext http,
            int page = 1, int pageSize = 25, CancellationToken ct = default)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            // A user who is not staff only ever sees their own operations, whatever filter they send.
            Guid? scope = principal.IsSupport ? userId : principal.UserId;
            OperationListResponse result = await service.SearchAsync(status, type, scope, page, pageSize, ct);
            return Results.Ok(result);
        }

        private static async Task<IResult> CreateAsync(CreateOperationRequest request, [FromServices] IOperationService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            OperationCreatedResponse created = await service.CreateAsync(
                request, principal.UserId, http.GetIpAddress(), http.GetCorrelationId(), ct);

            return Results.Created($"/api/device-operations/{created.Id}", created);
        }

        private static async Task<IResult> GetAsync(Guid id, [FromServices] IOperationService service, HttpContext http, CancellationToken ct)
        {
            OperationDto? operation = await service.GetAsync(id, ct);
            if (operation is null) return Results.NotFound();

            BackendPrincipal principal = http.RequirePrincipal();
            if (!principal.IsSupport && operation.UserId != principal.UserId)
            {
                // Users only see their own operations unless they hold support permissions.
                return Results.NotFound();
            }

            return Results.Ok(operation);
        }

        private static async Task<IResult> GetEventsAsync(Guid id, [FromServices] IOperationService service, HttpContext http, CancellationToken ct)
        {
            OperationDto? operation = await service.GetAsync(id, ct);
            if (operation is null) return Results.NotFound();

            BackendPrincipal principal = http.RequirePrincipal();
            if (!principal.IsSupport && operation.UserId != principal.UserId)
            {
                return Results.NotFound();
            }

            return Results.Ok(await service.GetEventsAsync(id, ct));
        }

        private static async Task<IResult> CancelAsync(Guid id, [FromServices] IOperationService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            CancelOperationResponse response = await service.RequestCancelAsync(
                id, principal.UserId, http.GetCorrelationId(),
                principal.HasPermission(Permissions.OperationsCancel), ct);
            return response.Cancelled || response.Status is not "NotFound"
                ? Results.Ok(response)
                : Results.NotFound(response);
        }

        private sealed record OperationResultRequest(
            int? ExitCode,
            bool TimedOut,
            bool Cancelled,
            string? Output,
            string? ErrorOutput,
            string? Summary,
            bool Verified);

        private static async Task<IResult> RecordResultAsync(Guid id, OperationResultRequest request, [FromServices] IOperationService service, HttpContext http, CancellationToken ct)
        {
            OperationDto? existing = await service.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            BackendPrincipal principal = http.RequirePrincipal();
            if (!principal.IsSupport && existing.UserId != principal.UserId)
            {
                // Users only record results for their own operations unless they hold support permissions.
                return Results.NotFound();
            }

            OperationDto? operation = await service.RecordResultAsync(
                id, request.ExitCode, request.TimedOut, request.Cancelled,
                request.Output, request.ErrorOutput, request.Summary, request.Verified, ct);

            return operation is null ? Results.NotFound() : Results.Ok(operation);
        }

        private static async Task<IResult> SearchReportsAsync(
            string? module, Guid? userId,
            [FromServices] IReportService service, HttpContext http,
            int page = 1, int pageSize = 25, CancellationToken ct = default)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            // A user who is not staff only sees their own reports.
            Guid? scope = principal.IsSupport ? userId : principal.UserId;
            ReportListResponse result = await service.SearchAsync(module, scope, page, pageSize, ct);
            return Results.Ok(result);
        }

        private static async Task<IResult> CreateReportAsync(CreateReportRequest request, [FromServices] IReportService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            DiagnosticReportDto created = await service.CreateAsync(request, principal.UserId, http.GetCorrelationId(), ct);
            return Results.Created($"/api/diagnostic-reports/{created.Id}", created);
        }

        private static async Task<IResult> GetReportAsync(Guid id, [FromServices] IReportService service, HttpContext http, CancellationToken ct)
        {
            DiagnosticReportDto? report = await service.GetAsync(id, ct);
            if (report is null) return Results.NotFound();

            BackendPrincipal principal = http.RequirePrincipal();
            if (!principal.IsSupport && report.UserId != principal.UserId)
            {
                return Results.NotFound();
            }

            return Results.Ok(report);
        }
    }
}
