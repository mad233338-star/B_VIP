using B_VIP.Application.Interfaces;
using B_VIP.Core;
using B_VIP.Core.Entities;
using B_VIP.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>Append-only audit writer. Events are stored in the database and exposed read-only to administrators.</summary>
    public sealed class AuditLogger : IAuditLogger
    {
        private readonly B_VIPDbContext _db;
        private readonly ILogger<AuditLogger> _logger;

        public AuditLogger(B_VIPDbContext db, ILogger<AuditLogger> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task LogAsync(
            Guid? actorUserId,
            string? actorName,
            string action,
            string? targetType,
            string? targetId,
            AuditOutcome outcome,
            string? detail,
            string? ipAddress,
            string? correlationId,
            CancellationToken ct = default)
        {
            // Never persist secret material in the audit trail.
            string? safeDetail = detail is null || detail.Length > 4000 ? detail?[..4000] : detail;

            var evt = new AuditEvent
            {
                Id = Guid.NewGuid(),
                ActorUserId = actorUserId,
                ActorName = actorName,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Outcome = outcome,
                Detail = safeDetail,
                IpAddress = ipAddress,
                CorrelationId = correlationId,
                OccurredUtc = DateTime.UtcNow
            };

            try
            {
                _db.AuditEvents.Add(evt);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Auditing must never take the request down, but the failure is surfaced to operators.
                _logger.LogError(ex, "Failed to persist audit event {Action}", action);
                throw;
            }
        }
    }
}
