using System.Text.Json;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Core.Common;

namespace B_VIP.Backend.Middleware
{
    /// <summary>
    /// Converts domain and unexpected exceptions into a consistent problem payload.
    /// Internal details are logged but never leaked to the client.
    /// </summary>
    public sealed class ExceptionMiddleware
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionMiddleware> _logger;
        private readonly IHostEnvironment _environment;

        public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IHostEnvironment environment)
        {
            _next = next;
            _logger = logger;
            _environment = environment;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (DomainException ex)
            {
                _logger.LogWarning(ex, "Domain error {Code} on {Path}", ex.Code, context.Request.Path.Value);
                await WriteAsync(context, ex.StatusCode, new ApiError(ex.Code, ex.Message, context.GetCorrelationId()));
            }
            catch (BadHttpRequestException ex)
            {
                _logger.LogWarning(ex, "Bad request on {Path}", context.Request.Path.Value);
                await WriteAsync(context, 400, new ApiError("bad-request", "The request body is malformed or too large.", context.GetCorrelationId()));
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            {
                _logger.LogWarning("Concurrency conflict on {Path}", context.Request.Path.Value);
                await WriteAsync(context, 409, new ApiError("concurrency-conflict",
                    "The record was modified by someone else. Reload and try again.", context.GetCorrelationId()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception on {Path} ({CorrelationId})",
                    context.Request.Path.Value, context.GetCorrelationId());

                string message = _environment.IsDevelopment()
                    ? ex.Message
                    : "An unexpected error occurred. The incident was logged.";

                await WriteAsync(context, 500, new ApiError("internal-error", message, context.GetCorrelationId()));
            }
        }

        private static async Task WriteAsync(HttpContext context, int status, ApiError error)
        {
            if (context.Response.HasStarted) return;

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new ApiErrorEnvelope(error), JsonOptions));
        }
    }
}
