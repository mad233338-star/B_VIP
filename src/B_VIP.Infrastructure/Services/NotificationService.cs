using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Admin;
using B_VIP.Core;
using B_VIP.Core.Entities;
using B_VIP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>In-application notifications. Every notification is stored with its creation time and read state.</summary>
    public sealed class NotificationService : INotificationService
    {
        private readonly B_VIPDbContext _db;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(B_VIPDbContext db, ILogger<NotificationService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task NotifyAsync(Guid userId, string type, string title, string body, string? link = null, string? source = null, CancellationToken ct = default)
        {
            if (!Enum.TryParse<NotificationType>(type, ignoreCase: true, out NotificationType parsed))
            {
                parsed = NotificationType.Administrative;
            }

            _db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Type = parsed,
                Title = Truncate(title, 256) ?? string.Empty,
                Body = Truncate(body, 2000) ?? string.Empty,
                Link = Truncate(link, 512),
                Source = Truncate(source, 64),
                CreatedUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
        }

        public async Task NotifyRoleAsync(string roleName, string type, string title, string body, string? link = null, string? source = null, CancellationToken ct = default)
        {
            List<Guid> userIds = await _db.UserRoles
                .Where(ur => ur.Role.NormalizedName == roleName.ToUpperInvariant())
                .Select(ur => ur.UserId)
                .Distinct()
                .ToListAsync(ct);

            foreach (Guid userId in userIds)
            {
                await NotifyAsync(userId, type, title, body, link, source, ct);
            }
        }

        public async Task<NotificationListResponse> SearchAsync(Guid userId, bool unreadOnly, int page, int pageSize, CancellationToken ct = default)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            IQueryable<Notification> query = _db.Notifications.Where(n => n.UserId == userId);
            if (unreadOnly)
            {
                query = query.Where(n => n.ReadUtc == null);
            }

            int total = await query.CountAsync(ct);
            int unread = await _db.Notifications.CountAsync(n => n.UserId == userId && n.ReadUtc == null, ct);

            List<Notification> items = await query
                .OrderByDescending(n => n.CreatedUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return new NotificationListResponse(items.Select(Map).ToList(), unread, total, page, pageSize);
        }

        public async Task<bool> MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default)
        {
            Notification? notification = await _db.Notifications
                .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

            if (notification is null || notification.ReadUtc is not null) return false;

            notification.ReadUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
        {
            List<Notification> items = await _db.Notifications
                .Where(n => n.UserId == userId && n.ReadUtc == null)
                .ToListAsync(ct);

            DateTime now = DateTime.UtcNow;
            foreach (Notification notification in items)
            {
                notification.ReadUtc = now;
            }

            await _db.SaveChangesAsync(ct);
            return items.Count;
        }

        public async Task<int> PurgeExpiredAsync(DateTime olderThanUtc, CancellationToken ct = default)
        {
            List<Notification> items = await _db.Notifications
                .Where(n => n.CreatedUtc < olderThanUtc && n.ReadUtc != null)
                .ToListAsync(ct);

            _db.Notifications.RemoveRange(items);
            await _db.SaveChangesAsync(ct);
            return items.Count;
        }

        private static NotificationDto Map(Notification n) => new(
            n.Id, n.Type.ToString(), n.Title, n.Body, n.Link, n.Source, n.CreatedUtc, n.ReadUtc);

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }
}
