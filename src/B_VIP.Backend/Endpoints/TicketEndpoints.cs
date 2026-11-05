using B_VIP.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Tickets;
using B_VIP.Core;

namespace B_VIP.Backend.Endpoints
{
    public static class TicketEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPTicketEndpoints(this IEndpointRouteBuilder endpoints)
        {
            RouteGroupBuilder group = endpoints.MapGroup("/api/tickets").WithTags("Support");

            group.MapGet("/", SearchAsync)
                .WithName("SearchTickets")
                .RequirePermission(Permissions.TicketsRead);

            group.MapGet("/categories", async ([FromServices] ITicketService service, CancellationToken ct) =>
                    Results.Ok(await service.GetCategoriesAsync(ct)))
                .WithName("GetTicketCategories")
                .RequirePermission(Permissions.TicketsRead);

            group.MapPost("/", CreateAsync)
                .WithName("CreateTicket")
                .RequirePermission(Permissions.TicketsWrite);

            group.MapGet("/{id:guid}", GetAsync)
                .WithName("GetTicket")
                .RequirePermission(Permissions.TicketsRead);

            group.MapPost("/{id:guid}/messages", AddMessageAsync)
                .WithName("AddTicketMessage")
                .RequirePermission(Permissions.TicketsWrite);

            group.MapPatch("/{id:guid}/status", UpdateStatusAsync)
                .WithName("UpdateTicketStatus")
                .RequirePermission(Permissions.TicketsWrite);

            group.MapPost("/{id:guid}/assign", AssignAsync)
                .WithName("AssignTicket")
                .RequirePermission(Permissions.TicketsAssign);

            return endpoints;
        }

        private static async Task<IResult> SearchAsync(
            string? status, string? priority, string? q,
            [FromServices] ITicketService service,
            HttpContext http,
            int page = 1, int pageSize = 25, bool mine = false, CancellationToken ct = default)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            bool canSeeAll = principal.HasPermission(Permissions.TicketsAll);
            bool scopeToMine = mine || !canSeeAll;

            TicketListResponse result = await service.SearchAsync(
                status, priority, principal.UserId, scopeToMine, q, page, pageSize, ct);

            return Results.Ok(result);
        }

        private static async Task<IResult> CreateAsync(CreateTicketRequest request, [FromServices] ITicketService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            TicketDto ticket = await service.CreateAsync(request, principal.UserId, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return Results.Created($"/api/tickets/{ticket.Id}", ticket);
        }

        private static async Task<IResult> GetAsync(Guid id, [FromServices] ITicketService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            TicketDetailDto? ticket = await service.GetAsync(id, principal.UserId, principal.HasPermission(Permissions.TicketsAll), ct);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }

        private static async Task<IResult> AddMessageAsync(Guid id, AddMessageRequest request, [FromServices] ITicketService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            TicketMessageDto? message = await service.AddMessageAsync(
                id, request, principal.UserId, principal.IsSupport, http.GetIpAddress(), http.GetCorrelationId(), ct);

            return message is null ? Results.NotFound() : Results.Created($"/api/tickets/{id}/messages", message);
        }

        private static async Task<IResult> UpdateStatusAsync(Guid id, UpdateTicketStatusRequest request, [FromServices] ITicketService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            TicketDto? ticket = await service.UpdateStatusAsync(id, request.Status, principal.UserId, principal.IsSupport, http.GetCorrelationId(), ct);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }

        private static async Task<IResult> AssignAsync(Guid id, AssignTicketRequest request, [FromServices] ITicketService service, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            TicketDto? ticket = await service.AssignAsync(id, request, principal.UserId, http.GetCorrelationId(), ct);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }
    }
}
