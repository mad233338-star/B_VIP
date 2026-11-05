using B_VIP.Backend.Auth;
using B_VIP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace B_VIP.Backend.Middleware
{
    /// <summary>
    /// Blocks ordinary traffic while maintenance mode is enabled. Administrators and health checks
    /// keep working so the operator can turn the mode off again. State is read at most every 30 seconds.
    /// </summary>
    public sealed class MaintenanceMiddleware
    {
        private static readonly object Gate = new();
        private static DateTime _lastReadUtc = DateTime.MinValue;
        private static bool _enabled;
        private static string? _message;

        private readonly RequestDelegate _next;
        private readonly ILogger<MaintenanceMiddleware> _logger;

        public MaintenanceMiddleware(RequestDelegate next, ILogger<MaintenanceMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopeFactory)
        {
            string path = context.Request.Path.Value ?? string.Empty;

            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshIfNeededAsync(scopeFactory);

                if (_enabled)
                {
                    context.Response.StatusCode = 503;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = new
                        {
                            code = "maintenance",
                            message = _message ?? "The service is in maintenance mode. Please try again later.",
                            correlationId = context.GetCorrelationId()
                        }
                    });
                    return;
                }
            }

            await _next(context);
        }

        private async Task RefreshIfNeededAsync(IServiceScopeFactory scopeFactory)
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _lastReadUtc < TimeSpan.FromSeconds(30)) return;
                _lastReadUtc = DateTime.UtcNow;
            }

            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                B_VIPDbContext db = scope.ServiceProvider.GetRequiredService<B_VIPDbContext>();
                var setting = await db.MaintenanceSettings.AsNoTracking().FirstOrDefaultAsync();

                bool enabled = setting?.MaintenanceEnabled == true;
                string? message = setting?.Message;

                lock (Gate)
                {
                    _enabled = enabled;
                    _message = message;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read the maintenance state; assuming it is disabled");
                lock (Gate)
                {
                    _enabled = false;
                    _message = null;
                }
            }
        }
    }
}
