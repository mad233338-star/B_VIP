using System.Text.Json;
using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Common;

namespace B_VIP.Backend.Auth
{
    /// <summary>
    /// Deny-by-default authentication: every /api route requires a valid bearer token except the
    /// explicitly published anonymous endpoints. The token is validated server side against the
    /// signing key and its claims are attached to the request.
    /// </summary>
    public sealed class AuthenticationMiddleware
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly string[] AnonymousPrefixes =
        [
            "/api/auth/login",
            "/api/auth/register",
            "/api/auth/verify",
            "/api/auth/resend-verification",
            "/api/auth/refresh",
            "/api/auth/forgot-password",
            "/api/auth/reset-password",
            "/api/health",
            "/api/meta",
            "/api/bootstrap"
        ];

        private readonly RequestDelegate _next;

        public AuthenticationMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, ITokenProvider tokenProvider)
        {
            string path = context.Request.Path.Value ?? string.Empty;
            bool isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
            bool anonymousAllowed = AnonymousPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!isApi)
            {
                await _next(context);
                return;
            }

            string? header = context.Request.Headers.Authorization;
            if (string.IsNullOrWhiteSpace(header))
            {
                if (anonymousAllowed)
                {
                    await _next(context);
                    return;
                }

                await WriteErrorAsync(context, 401, "unauthorized", "A valid access token is required.");
                return;
            }

            string token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : header.Trim();

            TokenValidationResult? result = tokenProvider.Validate(token);
            if (result is null)
            {
                if (anonymousAllowed)
                {
                    await _next(context);
                    return;
                }

                await WriteErrorAsync(context, 401, "invalid-token", "The access token is missing, expired or invalid.");
                return;
            }

            context.SetPrincipal(new BackendPrincipal(
                result.UserId,
                result.SessionId,
                result.UserName,
                result.Roles,
                result.Permissions,
                context.GetIpAddress(),
                context.GetCorrelationId()));

            await _next(context);
        }

        private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new ApiErrorEnvelope(new ApiError(code, message, context.GetCorrelationId())), JsonOptions));
        }
    }
}
