using System.Text.Json;
using B_VIP.Contracts.Common;

namespace B_VIP.Backend.Auth
{
    /// <summary>Deny-by-default permission gate applied to individual endpoints.</summary>
    public sealed class PermissionEndpointFilter : IEndpointFilter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly string[] _permissions;
        private readonly bool _requireAny;

        public PermissionEndpointFilter(string permission, params string[] additional)
        {
            _permissions = new[] { permission }.Concat(additional ?? Array.Empty<string>()).ToArray();
            _requireAny = true;
        }

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            HttpContext http = context.HttpContext;
            BackendPrincipal? principal = http.GetPrincipal();

            if (principal is null)
            {
                return Error(http, 401, "unauthorized", "A valid access token is required.");
            }

            bool allowed = _requireAny
                ? principal.HasAnyPermission(_permissions)
                : _permissions.All(principal.HasPermission);

            if (!allowed)
            {
                return Error(http, 403, "forbidden",
                    $"This operation requires the '{string.Join("' or '", _permissions)}' permission.");
            }

            return await next(context);
        }

        internal static IResult Error(HttpContext http, int status, string code, string message)
        {
            http.Response.StatusCode = status;
            return Results.Json(
                new ApiErrorEnvelope(new ApiError(code, message, http.GetCorrelationId())),
                JsonOptions,
                statusCode: status);
        }
    }

    public static class PermissionEndpointFilterExtensions
    {
        public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission, params string[] additional)
            where TBuilder : IEndpointConventionBuilder
        {
            builder.AddEndpointFilter(new PermissionEndpointFilter(permission, additional));
            return builder;
        }
    }
}
