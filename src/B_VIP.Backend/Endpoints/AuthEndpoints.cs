using B_VIP.Application.Interfaces;
using B_VIP.Backend.Auth;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Core.Common;

namespace B_VIP.Backend.Endpoints
{
    public static class AuthEndpoints
    {
        public static IEndpointRouteBuilder MapB_VIPAuthEndpoints(this IEndpointRouteBuilder endpoints)
        {
            RouteGroupBuilder group = endpoints.MapGroup("/api/auth").WithTags("Auth");

            group.MapPost("/register", RegisterAsync)
                .WithName("RegisterTechnician")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            group.MapGet("/verify", VerifyEmailAsync)
                .WithName("VerifyEmail")
                .AllowAnonymous();

            // The 6-digit code from the activation e-mail; works without opening the browser link.
            group.MapPost("/verify-code", VerifyEmailCodeAsync)
                .WithName("VerifyEmailCode")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            group.MapPost("/resend-verification", ResendVerificationAsync)
                .WithName("ResendVerification")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            group.MapPost("/login", LoginAsync)
                .WithName("Login")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            group.MapPost("/refresh", RefreshAsync)
                .WithName("Refresh")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            group.MapPost("/logout", LogoutAsync)
                .WithName("Logout")
                .AllowAnonymous();

            group.MapPost("/forgot-password", ForgotPasswordAsync)
                .WithName("ForgotPassword")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            group.MapPost("/reset-password", ResetPasswordAsync)
                .WithName("ResetPassword")
                .RequireRateLimiting("auth")
                .AllowAnonymous();

            group.MapGet("/me", MeAsync)
                .WithName("Me");

            group.MapPost("/change-password", ChangePasswordAsync)
                .WithName("ChangePassword");

            return endpoints;
        }

        private static async Task<IResult> RegisterAsync(
            RegisterTechnicianRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            RegisterTechnicianResponse response = await auth.RegisterTechnicianAsync(
                request, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return Results.Ok(response);
        }

        /// <summary>Activation link target: it is opened in a browser, so it answers with a page.</summary>
        private static async Task<IResult> VerifyEmailAsync(
            string? token, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            VerifyEmailResponse response = await auth.VerifyEmailAsync(
                token ?? string.Empty, http.GetIpAddress(), http.GetCorrelationId(), ct);

            string colour = response.Verified ? "#22C55E" : "#EF4444";
            string heading = response.Verified ? "E-mail verified" : "Activation failed";

            string html = $"""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>B_VIP Tool — {heading}</title></head>
                <body style="margin:0;background:#0B0F19;color:#FFFFFF;font-family:'Segoe UI',Arial,sans-serif;">
                  <div style="max-width:520px;margin:12vh auto;padding:32px;background:#111827;border:1px solid #24303F;border-radius:10px;">
                    <div style="font-size:12px;letter-spacing:2px;color:#00E5FF;font-weight:600;">B_VIP TOOL</div>
                    <h1 style="font-size:22px;margin:14px 0 8px;color:{colour};">{heading}</h1>
                    <p style="color:#94A3B8;line-height:1.6;">{System.Net.WebUtility.HtmlEncode(response.Message)}</p>
                    <p style="color:#94A3B8;line-height:1.6;">You can now return to the B_VIP Tool desktop client and sign in.</p>
                  </div>
                </body></html>
                """;

            return Results.Content(html, "text/html");
        }

        private static async Task<IResult> VerifyEmailCodeAsync(
            VerifyEmailCodeRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            VerifyEmailResponse response = await auth.VerifyEmailCodeAsync(
                request, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return Results.Ok(response);
        }

        private static async Task<IResult> ResendVerificationAsync(
            ResendVerificationRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            ResendVerificationResponse response = await auth.ResendVerificationAsync(
                request.Email, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return Results.Ok(response);
        }

        private static async Task<IResult> LoginAsync(LoginRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            SessionResponse session = await auth.LoginAsync(
                request,
                http.GetIpAddress(),
                http.Request.Headers.UserAgent,
                http.GetCorrelationId(),
                ct);

            return Results.Ok(session);
        }

        private static async Task<IResult> RefreshAsync(RefreshRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            SessionResponse session = await auth.RefreshAsync(
                request.RefreshToken,
                http.GetIpAddress(),
                http.Request.Headers.UserAgent,
                http.GetCorrelationId(),
                ct);

            return Results.Ok(session);
        }

        private static async Task<IResult> LogoutAsync(IAuthService auth, HttpContext http, CancellationToken ct)
        {
            string? refreshToken = null;
            if (http.Request.HasJsonContentType())
            {
                try
                {
                    LogoutRequest? body = await http.Request.ReadFromJsonAsync<LogoutRequest>(ct);
                    refreshToken = body?.RefreshToken;
                }
                catch (System.Text.Json.JsonException)
                {
                    // A malformed body must not prevent signing out the presented token.
                }
            }

            BackendPrincipal? principal = http.GetPrincipal();
            await auth.LogoutAsync(refreshToken, principal?.UserId, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return Results.NoContent();
        }

        private static async Task<IResult> ForgotPasswordAsync(ForgotPasswordRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            (bool success, string message) = await auth.RequestPasswordResetAsync(request.Email, http.GetIpAddress(), ct);
            return success ? Results.Ok(new { message }) : Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        private static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            (bool success, string message) = await auth.ResetPasswordAsync(request, http.GetIpAddress(), ct);
            return success
                ? Results.Ok(new { message })
                : Results.BadRequest(new ApiErrorEnvelope(new ApiError("reset-failed",
                    message == "reset-token-invalid"
                        ? "The reset link is invalid or has expired."
                        : "The password does not satisfy the policy.",
                    http.GetCorrelationId())));
        }

        private static async Task<IResult> MeAsync(IAuthService auth, IUserAdminService users, ILicenseService licenses, IConfiguration configuration, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();

            Contracts.Users.UserProfileDto? profile = await users.GetUserAsync(principal.UserId, ct);
            if (profile is null)
            {
                return Results.NotFound(new ApiErrorEnvelope(new ApiError("user-not-found", "The account no longer exists.", http.GetCorrelationId())));
            }

            Contracts.Auth.LicenseSummaryDto? license = await licenses.GetSummaryForUserAsync(principal.UserId, ct);

            UserSummaryDto summary = new(
                profile.Id,
                profile.UserName,
                profile.DisplayName,
                profile.Email,
                profile.Status,
                profile.Roles,
                profile.CreatedUtc,
                profile.LastLoginUtc,
                profile.EmailVerified,
                profile.HardwareId,
                profile.HardwareIdResetCount);

            string version = typeof(AuthEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0";

            return Results.Ok(new MeResponse(
                summary,
                principal.Permissions,
                license,
                configuration["locale:default-language"] ?? "en",
                version));
        }

        private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
        {
            BackendPrincipal principal = http.RequirePrincipal();
            SessionResponse session = await auth.ChangePasswordAsync(principal.UserId, request, http.GetIpAddress(), http.GetCorrelationId(), ct);
            return Results.Ok(session);
        }

        private sealed record LogoutRequest(string? RefreshToken);
    }
}
