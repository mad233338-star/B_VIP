using B_VIP.Contracts.Common;
using B_VIP.Contracts.Tickets;

namespace B_VIP.Application.Interfaces
{
    public interface ITicketService
    {
        Task<TicketDto> CreateAsync(CreateTicketRequest request, Guid userId, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<TicketListResponse> SearchAsync(string? status, string? priority, Guid? userId, bool mineOnly, string? query, int page, int pageSize, CancellationToken ct = default);

        Task<TicketDetailDto?> GetAsync(Guid id, Guid requesterUserId, bool canSeeAll, CancellationToken ct = default);

        Task<TicketMessageDto?> AddMessageAsync(Guid id, AddMessageRequest request, Guid userId, bool isStaff, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<TicketDto?> UpdateStatusAsync(Guid id, string status, Guid userId, bool isStaff, string? correlationId, CancellationToken ct = default);

        Task<TicketDto?> AssignAsync(Guid id, AssignTicketRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<SupportCategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    }
}
