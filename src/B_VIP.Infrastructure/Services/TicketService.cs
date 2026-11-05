using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Tickets;
using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.Core.Entities;
using B_VIP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>Support centre: tickets, conversation, assignment and status handling.</summary>
    public sealed class TicketService : ITicketService
    {
        private readonly B_VIPDbContext _db;
        private readonly IAuditLogger _audit;
        private readonly INotificationService _notifications;
        private readonly ILogger<TicketService> _logger;

        public TicketService(B_VIPDbContext db, IAuditLogger audit, INotificationService notifications, ILogger<TicketService> logger)
        {
            _db = db;
            _audit = audit;
            _notifications = notifications;
            _logger = logger;
        }

        public async Task<TicketDto> CreateAsync(CreateTicketRequest request, Guid userId, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.Title) || string.IsNullOrWhiteSpace(request.Description))
            {
                throw new DomainException("invalid-request", "Title and description are required.");
            }

            if (request.Title.Trim().Length > 256 || request.Description.Trim().Length > 8000)
            {
                throw new DomainException("too-long", "The title or description exceeds the allowed length.");
            }

            if (!Enum.TryParse<TicketPriority>(request.Priority, true, out TicketPriority priority))
            {
                priority = TicketPriority.Normal;
            }

            if (request.DiagnosticReportId.HasValue &&
                !await _db.DiagnosticReports.AnyAsync(r => r.Id == request.DiagnosticReportId, ct))
            {
                throw new DomainException("report-not-found", "The referenced diagnostic report does not exist.");
            }

            DateTime now = DateTime.UtcNow;
            var ticket = new ServiceTicket
            {
                Id = Guid.NewGuid(),
                Number = $"TCK-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
                UserId = userId,
                CategoryId = request.CategoryId,
                Title = request.Title.Trim(),
                Description = request.Description.Trim(),
                Priority = priority,
                Status = TicketStatus.Open,
                CreatedUtc = now,
                UpdatedUtc = now,
                LastMessageUtc = now,
                DiagnosticReportId = request.DiagnosticReportId,
                CorrelationId = correlationId
            };

            ticket.Messages.Add(new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                AuthorUserId = userId,
                Body = request.Description.Trim(),
                IsInternalNote = false,
                CreatedUtc = now
            });

            _db.ServiceTickets.Add(ticket);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "ticket.create", "ticket", ticket.Id.ToString(),
                AuditOutcome.Success, $"{ticket.Number}: {ticket.Title}", ipAddress, correlationId, ct);

            try
            {
                await _notifications.NotifyRoleAsync(RoleNames.SupportStaff, NotificationType.Support.ToString(),
                    $"New ticket {ticket.Number}", ticket.Title, $"/support-center/{ticket.Id}", "support", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not notify support staff about ticket {Number}", ticket.Number);
            }

            TicketDto? created = await GetInternalAsync(ticket.Id, ct);
            return created!;
        }

        public async Task<TicketListResponse> SearchAsync(string? status, string? priority, Guid? userId, bool mineOnly, string? query, int page, int pageSize, CancellationToken ct = default)
        {
            page = Paging.NormalizePage(page);
            pageSize = Paging.NormalizePageSize(pageSize);

            IQueryable<ServiceTicket> tickets = _db.ServiceTickets.AsNoTracking();

            if (userId.HasValue)
            {
                tickets = mineOnly
                    ? tickets.Where(t => t.UserId == userId.Value)
                    : tickets.Where(t => t.UserId == userId.Value || t.AssignedToUserId == userId.Value ||
                                         _db.UserRoles.Any(ur => ur.UserId == userId.Value &&
                                             (ur.Role.NormalizedName == RoleNames.SupportStaff.ToUpperInvariant() ||
                                              ur.Role.NormalizedName == RoleNames.Supervisor.ToUpperInvariant() ||
                                              ur.Role.NormalizedName == RoleNames.GeneralManager.ToUpperInvariant() ||
                                              ur.Role.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant())));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s).ToList();
                tickets = tickets.Where(t => wanted.Contains(t.Status.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(priority) && Enum.TryParse<TicketPriority>(priority, true, out TicketPriority p))
            {
                tickets = tickets.Where(t => t.Priority == p);
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                string term = query.Trim().ToUpperInvariant();
                tickets = tickets.Where(t => t.Title.ToUpper().Contains(term) ||
                                             t.Number.ToUpper().Contains(term) ||
                                             t.Description.ToUpper().Contains(term));
            }

            int total = await tickets.CountAsync(ct);
            List<ServiceTicket> items = await tickets
                .OrderByDescending(t => t.UpdatedUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(t => t.Category)
                .Include(t => t.AssignedTo)
                .Include(t => t.User)
                .Include(t => t.Messages)
                .ToListAsync(ct);

            return new TicketListResponse(items.Select(Map).ToList(), total, page, pageSize);
        }

        public async Task<TicketDetailDto?> GetAsync(Guid id, Guid requesterUserId, bool canSeeAll, CancellationToken ct = default)
        {
            ServiceTicket? ticket = await _db.ServiceTickets.AsNoTracking()
                .Include(t => t.Category)
                .Include(t => t.AssignedTo)
                .Include(t => t.User)
                .Include(t => t.Messages).ThenInclude(m => m.Author)
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            if (ticket is null) return null;

            bool visible = canSeeAll || ticket.UserId == requesterUserId || ticket.AssignedToUserId == requesterUserId;
            if (!visible) return null;

            var attachments = await _db.TicketAttachments.AsNoTracking()
                .Where(a => a.TicketId == id)
                .ToListAsync(ct);

            IReadOnlyList<TicketMessageDto> messages = ticket.Messages
                .OrderBy(m => m.CreatedUtc)
                .Select(m => new TicketMessageDto(
                    m.Id,
                    m.AuthorUserId,
                    m.Author?.DisplayName ?? string.Empty,
                    m.Body,
                    // Internal notes are never exposed to the ticket owner.
                    canSeeAll && m.IsInternalNote || !m.IsInternalNote,
                    m.CreatedUtc,
                    attachments.Where(a => a.TicketMessageId == m.Id)
                        .Select(a => new TicketAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedUtc))
                        .ToList()))
                .ToList();

            return new TicketDetailDto(Map(ticket), messages);
        }

        public async Task<TicketMessageDto?> AddMessageAsync(Guid id, AddMessageRequest request, Guid userId, bool isStaff, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.Body))
            {
                throw new DomainException("invalid-request", "The message body is required.");
            }

            ServiceTicket? ticket = await _db.ServiceTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (ticket is null) return null;

            bool isOwner = ticket.UserId == userId;
            if (!isStaff && !isOwner)
            {
                await _audit.LogAsync(userId, null, "ticket.message", "ticket", id.ToString(),
                    AuditOutcome.Denied, "Message from a non-participant.", ipAddress, correlationId, ct);
                throw new DomainException("forbidden", "You cannot post to this ticket.", 403);
            }

            if (request.IsInternalNote && !isStaff)
            {
                throw new DomainException("forbidden", "Only support staff can add internal notes.", 403);
            }

            DateTime now = DateTime.UtcNow;
            var message = new TicketMessage
            {
                Id = Guid.NewGuid(),
                TicketId = id,
                AuthorUserId = userId,
                Body = request.Body.Trim(),
                IsInternalNote = request.IsInternalNote,
                CreatedUtc = now
            };

            _db.TicketMessages.Add(message);
            ticket.LastMessageUtc = now;
            ticket.UpdatedUtc = now;
            ticket.LastMessageByUserId = userId;

            if (isStaff && ticket.Status is TicketStatus.Open or TicketStatus.WaitingForUser)
            {
                ticket.Status = TicketStatus.InProgress;
            }
            else if (!isStaff && ticket.Status is TicketStatus.WaitingForUser or TicketStatus.WaitingForSupport or TicketStatus.InProgress)
            {
                ticket.Status = TicketStatus.WaitingForSupport;
            }

            await _db.SaveChangesAsync(ct);

            Guid notifyTarget = isOwner ? (ticket.AssignedToUserId ?? Guid.Empty) : ticket.UserId;
            if (notifyTarget != Guid.Empty)
            {
                try
                {
                    await _notifications.NotifyAsync(notifyTarget, NotificationType.Support.ToString(),
                        $"New message on {ticket.Number}", Truncate(request.Body, 200) ?? "New message",
                        $"/support-center/{id}", "support", ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not notify about ticket message {TicketId}", id);
                }
            }

            await _audit.LogAsync(userId, null, isStaff ? "ticket.reply" : "ticket.message", "ticket", id.ToString(),
                AuditOutcome.Success, request.IsInternalNote ? "Internal note" : "Reply", ipAddress, correlationId, ct);

            User? author = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            return new TicketMessageDto(message.Id, userId, author?.DisplayName ?? string.Empty,
                message.Body, message.IsInternalNote, message.CreatedUtc, Array.Empty<TicketAttachmentDto>());
        }

        public async Task<TicketDto?> UpdateStatusAsync(Guid id, string status, Guid userId, bool isStaff, string? correlationId, CancellationToken ct = default)
        {
            if (!Enum.TryParse<TicketStatus>(status, true, out TicketStatus parsed))
            {
                throw new DomainException("invalid-status", $"Unknown ticket status '{status}'.");
            }

            ServiceTicket? ticket = await _db.ServiceTickets
                .Include(t => t.Category).Include(t => t.AssignedTo).Include(t => t.User).Include(t => t.Messages)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            if (ticket is null) return null;

            if (!isStaff)
            {
                throw new DomainException("forbidden", "Only support staff can change the ticket status.", 403);
            }

            if (ticket.Status == parsed) return Map(ticket);

            TicketStatus previous = ticket.Status;
            ticket.Status = parsed;
            ticket.UpdatedUtc = DateTime.UtcNow;
            ticket.ClosedUtc = parsed is TicketStatus.Closed or TicketStatus.Resolved ? DateTime.UtcNow : null;

            if (parsed == TicketStatus.Reopened)
            {
                ticket.Status = TicketStatus.InProgress;
                ticket.ClosedUtc = null;
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "ticket.status", "ticket", id.ToString(),
                AuditOutcome.Success, $"{previous} -> {ticket.Status}", null, correlationId, ct);

            try
            {
                await _notifications.NotifyAsync(ticket.UserId, NotificationType.Support.ToString(),
                    $"{ticket.Number} updated", $"Status: {ticket.Status}", $"/support-center/{id}", "support", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not notify about status change for {TicketId}", id);
            }

            return Map(ticket);
        }

        public async Task<TicketDto?> AssignAsync(Guid id, AssignTicketRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            ServiceTicket? ticket = await _db.ServiceTickets
                .Include(t => t.Category).Include(t => t.AssignedTo).Include(t => t.User).Include(t => t.Messages)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            if (ticket is null) return null;

            if (request.AssignedToUserId.HasValue)
            {
                bool isAssignable = await _db.UserRoles.AnyAsync(ur =>
                    ur.UserId == request.AssignedToUserId.Value &&
                    (ur.Role.NormalizedName == RoleNames.SupportStaff.ToUpperInvariant() ||
                     ur.Role.NormalizedName == RoleNames.Supervisor.ToUpperInvariant() ||
                     ur.Role.NormalizedName == RoleNames.GeneralManager.ToUpperInvariant() ||
                     ur.Role.NormalizedName == RoleNames.SuperAdmin.ToUpperInvariant()), ct);

                if (!isAssignable)
                {
                    throw new DomainException("invalid-assignee", "The target user is not a support member.", 422);
                }
            }

            Guid? previous = ticket.AssignedToUserId;
            ticket.AssignedToUserId = request.AssignedToUserId;
            ticket.UpdatedUtc = DateTime.UtcNow;

            if (request.AssignedToUserId.HasValue)
            {
                ticket.Assignments.Add(new TicketAssignment
                {
                    Id = Guid.NewGuid(),
                    TicketId = ticket.Id,
                    AssignedToUserId = request.AssignedToUserId.Value,
                    AssignedByUserId = actorUserId,
                    AssignedUtc = DateTime.UtcNow,
                    Comment = request.Comment
                });

                if (ticket.Status == TicketStatus.Open)
                {
                    ticket.Status = TicketStatus.InProgress;
                }
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "ticket.assign", "ticket", id.ToString(),
                AuditOutcome.Success, $"From {previous?.ToString() ?? "none"} to {request.AssignedToUserId?.ToString() ?? "none"}",
                null, correlationId, ct);

            if (request.AssignedToUserId.HasValue)
            {
                try
                {
                    await _notifications.NotifyAsync(request.AssignedToUserId.Value, NotificationType.Support.ToString(),
                        $"Ticket {ticket.Number} assigned to you", ticket.Title, $"/support-center/{id}", "support", ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not notify assignee for ticket {TicketId}", id);
                }
            }

            return Map(ticket);
        }

        public async Task<IReadOnlyList<SupportCategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
        {
            List<SupportCategory> categories = await _db.SupportCategories.AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct);

            var counts = await _db.ServiceTickets.Where(t => t.CategoryId != null)
                .GroupBy(t => t.CategoryId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key!.Value, x => x.Count, ct);

            return categories.Select(c => new SupportCategoryDto(
                c.Id, c.Name, c.NameAr, c.IsActive,
                counts.TryGetValue(c.Id, out int n) ? n : 0)).ToList();
        }

        private async Task<TicketDto?> GetInternalAsync(Guid id, CancellationToken ct)
        {
            ServiceTicket? ticket = await _db.ServiceTickets.AsNoTracking()
                .Include(t => t.Category)
                .Include(t => t.AssignedTo)
                .Include(t => t.User)
                .Include(t => t.Messages)
                .FirstOrDefaultAsync(t => t.Id == id, ct);

            return ticket is null ? null : Map(ticket);
        }

        private static TicketDto Map(ServiceTicket t) => new(
            t.Id,
            t.Number,
            t.Title,
            t.Description,
            t.Priority.ToString(),
            t.Status.ToString(),
            t.UserId,
            t.User?.UserName ?? string.Empty,
            t.Category?.Name,
            t.AssignedToUserId,
            t.AssignedTo?.DisplayName,
            t.CreatedUtc,
            t.UpdatedUtc,
            t.LastMessageUtc,
            t.DiagnosticReportId,
            t.Messages?.Count ?? 0);

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }
}
