using System.Net;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Licenses;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Reports;
using B_VIP.Contracts.Tickets;
using B_VIP.Contracts.Users;

namespace B_VIP.CloudClient
{
    /// <summary>Result of probing the unauthenticated health endpoint.</summary>
    public sealed record HealthProbe(HttpStatusCode StatusCode, bool Healthy, string? Body);

    public partial class BVipApiClient
    {
        // ---- Auth -------------------------------------------------------
        public async Task<SessionResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
        {
            SessionResponse? session = await PostAsync<SessionResponse>("api/auth/login", request, ct);

            // A 2xx is not authentication on its own. Some deployments answer every request with a
            // canned "success" payload; without a real token triple and a user identity there is no
            // session, so the call fails here instead of navigating into the application.
            if (!IsUsableSession(session))
            {
                throw new ApiException(HttpStatusCode.Unauthorized, "invalid-credentials",
                    "The cloud service did not return a valid session for these credentials.", null, null);
            }

            // A session for somebody else is not a session for this attempt. A service that answers
            // every request with the same canned administrator would otherwise let any typed pair of
            // credentials through, so the account that came back has to be the account that was asked
            // for by name or by address.
            if (!SessionBelongsToAccount(session!, request.UserName))
            {
                throw new ApiException(HttpStatusCode.Unauthorized, "invalid-credentials",
                    "The cloud service returned a session for a different account than the one that signed in.", null, null);
            }

            await SetTokensAsync(session!.Tokens, ct);
            return session;
        }

        /// <summary>True only for a session that carries a token and an identified account.</summary>
        private static bool IsUsableSession(SessionResponse? session)
            => session is not null &&
               session.Tokens is not null &&
               !string.IsNullOrWhiteSpace(session.Tokens.AccessToken) &&
               session.User is not null &&
               session.User.Id != Guid.Empty &&
               session.User.Roles.Count > 0;

        /// <summary>
        /// The account in the response must correspond to the identifier that was submitted (its user
        /// name or its e-mail address). This is what stops a blanket "success" answer from opening the
        /// admin console for an arbitrary typed login.
        /// </summary>
        private static bool SessionBelongsToAccount(SessionResponse session, string? submittedIdentifier)
        {
            string typed = (submittedIdentifier ?? string.Empty).Trim();
            if (typed.Length == 0)
            {
                return false;
            }

            return Same(session.User.UserName) || Same(session.User.Email) || Same(session.User.DisplayName);

            bool Same(string? value)
                => !string.IsNullOrWhiteSpace(value) &&
                   string.Equals(value.Trim(), typed, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<SessionResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            SessionResponse session = await PostAsync<SessionResponse>("api/auth/refresh", new RefreshRequest(refreshToken), ct);
            if (!IsUsableSession(session))
            {
                throw new ApiException(HttpStatusCode.Unauthorized, "invalid-credentials",
                    "The cloud service did not return a valid session for these credentials.", null, null);
            }

            await SetTokensAsync(session.Tokens, ct);
            return session;
        }

        public async Task LogoutAsync(string? refreshToken = null, CancellationToken ct = default)
        {
            try
            {
                await PostAsync<object>("api/auth/logout", new { refreshToken }, ct);
            }
            catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                // The session is already invalid on the server side.
            }

            await ClearSessionAsync(ct);
        }

        public Task<MeResponse> MeAsync(CancellationToken ct = default)
            => GetAsync<MeResponse>("api/auth/me", ct);

        public Task<MessageResponse> ForgotPasswordAsync(string email, CancellationToken ct = default)
            => PostAsync<MessageResponse>("api/auth/forgot-password", new ForgotPasswordRequest(email), ct);

        public Task<MessageResponse> ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default)
            => PostAsync<MessageResponse>("api/auth/reset-password", new ResetPasswordRequest(token, newPassword), ct);

        public async Task<SessionResponse> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
        {
            SessionResponse session = await PostAsync<SessionResponse>("api/auth/change-password",
                new ChangePasswordRequest(currentPassword, newPassword), ct);
            await SetTokensAsync(session.Tokens, ct);
            return session;
        }

        public Task<RegisterTechnicianResponse> RegisterTechnicianAsync(RegisterTechnicianRequest request, CancellationToken ct = default)
            => PostAsync<RegisterTechnicianResponse>("api/auth/register", request, ct);

        public Task<ResendVerificationResponse> ResendVerificationAsync(string email, CancellationToken ct = default)
            => PostAsync<ResendVerificationResponse>("api/auth/resend-verification", new ResendVerificationRequest(email), ct);

        /// <summary>Activates the account with the 6-digit code from the activation e-mail.</summary>
        public Task<VerifyEmailResponse> VerifyEmailCodeAsync(VerifyEmailCodeRequest request, CancellationToken ct = default)
            => PostAsync<VerifyEmailResponse>("api/auth/verify-code", request, ct);

        public Task<BootstrapStatusDto> GetBootstrapStatusAsync(CancellationToken ct = default)
            => GetAsync<BootstrapStatusDto>("api/bootstrap/status", ct);

        public async Task<SessionResponse> CreateFirstAdminAsync(FirstAdminRequest request, CancellationToken ct = default)
        {
            await PostAsync<MessageResponse>("api/bootstrap/first-admin", request, ct);
            // The bootstrap endpoint only acknowledges creation; sign the new account in normally.
            return await LoginAsync(new LoginRequest(request.UserName, request.Password), ct);
        }

        // ---- Users and roles -------------------------------------------
        public Task<PagedResponse<UserSummaryDto>> SearchUsersAsync(string? q, string? status, string? role, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<PagedResponse<UserSummaryDto>>(
                $"api/users?q={Uri.EscapeDataString(q ?? string.Empty)}&status={Uri.EscapeDataString(status ?? string.Empty)}&role={Uri.EscapeDataString(role ?? string.Empty)}&page={page}&pageSize={pageSize}", ct);

        public Task<UserProfileDto> GetUserAsync(Guid id, CancellationToken ct = default)
            => GetAsync<UserProfileDto>($"api/users/{id}", ct);

        public Task<UserProfileDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
            => PostAsync<UserProfileDto>("api/users", request, ct);

        public Task<UserProfileDto> UpdateUserAsync(Guid id, AdminUpdateUserRequest request, CancellationToken ct = default)
            => PatchAsync<UserProfileDto>($"api/users/{id}", request, ct);

        public Task SuspendUserAsync(Guid id, string reason, CancellationToken ct = default)
            => PostAsync<object>($"api/users/{id}/suspend", new SuspendUserRequest(reason), ct);

        public Task ReactivateUserAsync(Guid id, CancellationToken ct = default)
            => PostAsync<object>($"api/users/{id}/reactivate", new { }, ct);

        public Task RevokeSessionsAsync(Guid id, CancellationToken ct = default)
            => PostAsync<object>($"api/users/{id}/revoke-sessions", new { }, ct);

        public Task<IReadOnlyList<string>> AssignRolesAsync(Guid id, IReadOnlyList<string> roles, CancellationToken ct = default)
            => PutAsync<IReadOnlyList<string>>($"api/users/{id}/roles", new AssignRolesRequest(roles), ct);

        public Task<IReadOnlyList<SessionDto>> GetUserSessionsAsync(Guid id, CancellationToken ct = default)
            => GetAsync<IReadOnlyList<SessionDto>>($"api/users/{id}/sessions", ct);

        // ---- Technician administration ----------------------------------
        public Task<PagedResponse<UserSummaryDto>> SearchTechniciansAsync(string? q, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<PagedResponse<UserSummaryDto>>(
                $"api/admin/technicians?q={Uri.EscapeDataString(q ?? string.Empty)}&page={page}&pageSize={pageSize}", ct);

        public Task<TechnicianMetricsDto> GetTechnicianMetricsAsync(CancellationToken ct = default)
            => GetAsync<TechnicianMetricsDto>("api/admin/technicians/metrics", ct);

        public Task<ResetHardwareIdResponse> ResetHardwareIdAsync(Guid userId, CancellationToken ct = default)
            => PostAsync<ResetHardwareIdResponse>($"api/admin/technicians/{userId}/reset-hwid", new { }, ct);

        public Task<VerifyEmailResponseDto> SetEmailVerifiedAsync(Guid userId, bool verified, CancellationToken ct = default)
            => PostAsync<VerifyEmailResponseDto>($"api/admin/technicians/{userId}/verify-email",
                new SetEmailVerifiedRequest(verified), ct);

        public Task<ExtendSubscriptionResponse> ExtendSubscriptionAsync(Guid userId, int days, string? reason = null, CancellationToken ct = default)
            => PostAsync<ExtendSubscriptionResponse>($"api/admin/technicians/{userId}/extend-subscription",
                new ExtendSubscriptionRequest(days, reason), ct);

        public Task DeleteTechnicianAsync(Guid userId, CancellationToken ct = default)
            => DeleteAsync($"api/admin/technicians/{userId}", ct);

        public Task<LicenseDto> ChangeTechnicianPlanAsync(Guid userId, Guid planId, bool resetPeriod = true, CancellationToken ct = default)
            => PostAsync<LicenseDto>($"api/admin/technicians/{userId}/plan", new ChangePlanRequest(planId, resetPeriod), ct);

        public Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(CancellationToken ct = default)
            => GetAsync<IReadOnlyList<PermissionDto>>("api/admin/permissions", ct);

        public Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default)
            => GetAsync<IReadOnlyList<RoleDto>>("api/admin/roles", ct);

        public Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default)
            => PostAsync<RoleDto>("api/admin/roles", request, ct);

        public Task<RoleDto> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default)
            => PatchAsync<RoleDto>($"api/admin/roles/{id}", request, ct);

        public Task SetRolePermissionsAsync(Guid id, IReadOnlyList<string> permissions, CancellationToken ct = default)
            => PutAsync<object>($"api/admin/roles/{id}/permissions", new SetRolePermissionsRequest(permissions), ct);

        // ---- Licenses ---------------------------------------------------
        public Task<IReadOnlyList<LicensePlanDto>> GetPlansAsync(CancellationToken ct = default)
            => GetAsync<IReadOnlyList<LicensePlanDto>>("api/licenses/plans", ct);

        public Task<LicensePlanDto> CreatePlanAsync(CreatePlanRequest request, CancellationToken ct = default)
            => PostAsync<LicensePlanDto>("api/licenses/plans", request, ct);

        public Task<LicensePlanDto> UpdatePlanAsync(Guid id, UpdatePlanRequest request, CancellationToken ct = default)
            => PatchAsync<LicensePlanDto>($"api/licenses/plans/{id}", request, ct);

        public Task DeletePlanAsync(Guid id, CancellationToken ct = default)
            => DeleteAsync($"api/licenses/plans/{id}", ct);

        public Task<PagedResponse<LicenseDto>> SearchLicensesAsync(string? q, string? status, Guid? userId, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<PagedResponse<LicenseDto>>(
                $"api/licenses?q={Uri.EscapeDataString(q ?? string.Empty)}&status={Uri.EscapeDataString(status ?? string.Empty)}&userId={(userId.HasValue ? userId.Value.ToString() : string.Empty)}&page={page}&pageSize={pageSize}", ct);

        public Task<LicenseDto> CreateLicenseAsync(CreateLicenseRequest request, CancellationToken ct = default)
            => PostAsync<LicenseDto>("api/licenses", request, ct);

        public Task<LicenseDto> GetLicenseAsync(Guid id, CancellationToken ct = default)
            => GetAsync<LicenseDto>($"api/licenses/{id}", ct);

        public Task<LicenseDto> UpdateLicenseAsync(Guid id, UpdateLicenseRequest request, CancellationToken ct = default)
            => PatchAsync<LicenseDto>($"api/licenses/{id}", request, ct);

        public Task SuspendLicenseAsync(Guid id, string? reason, CancellationToken ct = default)
            => PostAsync<object>($"api/licenses/{id}/suspend", new LicenseActionRequest(reason), ct);

        public Task ReactivateLicenseAsync(Guid id, string? reason, CancellationToken ct = default)
            => PostAsync<object>($"api/licenses/{id}/reactivate", new LicenseActionRequest(reason), ct);

        public Task RevokeLicenseAsync(Guid id, string? reason, CancellationToken ct = default)
            => PostAsync<object>($"api/licenses/{id}/revoke", new LicenseActionRequest(reason), ct);

        public Task<VerifyLicenseResponse> VerifyLicenseAsync(VerifyLicenseRequest request, CancellationToken ct = default)
            => PostAsync<VerifyLicenseResponse>("api/licenses/verify", request, ct);

        public Task<ActivateLicenseResponse> ActivateLicenseAsync(ActivateLicenseRequest request, CancellationToken ct = default)
            => PostAsync<ActivateLicenseResponse>("api/licenses/activate", request, ct);

        public Task DeactivateLicenseAsync(DeactivateLicenseRequest request, CancellationToken ct = default)
            => PostAsync<object>("api/licenses/deactivate", request, ct);

        public Task<LicenseActivationListResponse> GetActivationsAsync(Guid licenseId, CancellationToken ct = default)
            => GetAsync<LicenseActivationListResponse>($"api/licenses/{licenseId}/activations", ct);

        public Task RemoveActivationAsync(Guid licenseId, Guid activationId, CancellationToken ct = default)
            => DeleteAsync($"api/licenses/{licenseId}/activations/{activationId}", ct);

        public Task<IReadOnlyList<LicenseVerificationEventDto>> GetVerificationEventsAsync(Guid licenseId, int limit = 50, CancellationToken ct = default)
            => GetAsync<IReadOnlyList<LicenseVerificationEventDto>>($"api/licenses/{licenseId}/verification-events?limit={limit}", ct);

        // ---- Operations and reports -------------------------------------

        /// <summary>
        /// The cloud service queue the technician's Service Operations screen polls. It returns
        /// <c>success</c>/<c>status</c>/<c>tasks</c> and answers 200 even for an empty queue.
        /// </summary>
        public Task<ServiceQueueResponse> GetServiceQueueAsync(CancellationToken ct = default)
            => GetAsync<ServiceQueueResponse>("api/operations/queue", ct);
        public Task<OperationCreatedResponse> CreateOperationAsync(CreateOperationRequest request, CancellationToken ct = default)
            => PostAsync<OperationCreatedResponse>("api/device-operations", request, ct);

        public Task<OperationListResponse> SearchOperationsAsync(string? status, string? type, Guid? userId, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<OperationListResponse>(
                $"api/device-operations?status={Uri.EscapeDataString(status ?? string.Empty)}&type={Uri.EscapeDataString(type ?? string.Empty)}&userId={(userId.HasValue ? userId.Value.ToString() : string.Empty)}&page={page}&pageSize={pageSize}", ct);

        public Task<OperationDto> GetOperationAsync(Guid id, CancellationToken ct = default)
            => GetAsync<OperationDto>($"api/device-operations/{id}", ct);

        public Task<IReadOnlyList<OperationEventDto>> GetOperationEventsAsync(Guid id, CancellationToken ct = default)
            => GetAsync<IReadOnlyList<OperationEventDto>>($"api/device-operations/{id}/events", ct);

        public Task<OperationDto> CancelOperationAsync(Guid id, CancellationToken ct = default)
            => PostAsync<OperationDto>($"api/device-operations/{id}/cancel", new { }, ct);

        public Task<OperationDto> RecordOperationResultAsync(Guid id, RecordOperationResultRequest request, CancellationToken ct = default)
            => PostAsync<OperationDto>($"api/device-operations/{id}/result", request, ct);

        public Task<ReportListResponse> SearchReportsAsync(string? module, Guid? userId, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<ReportListResponse>(
                $"api/diagnostic-reports?module={Uri.EscapeDataString(module ?? string.Empty)}&userId={(userId.HasValue ? userId.Value.ToString() : string.Empty)}&page={page}&pageSize={pageSize}", ct);

        public Task<DiagnosticReportDto> CreateReportAsync(CreateReportRequest request, CancellationToken ct = default)
            => PostAsync<DiagnosticReportDto>("api/diagnostic-reports", request, ct);

        public Task<DiagnosticReportDto> GetReportAsync(Guid id, CancellationToken ct = default)
            => GetAsync<DiagnosticReportDto>($"api/diagnostic-reports/{id}", ct);

        // ---- Support tickets ---------------------------------------------
        public Task<TicketListResponse> SearchTicketsAsync(string? status, string? priority, string? q, bool mine, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<TicketListResponse>(
                $"api/tickets?status={Uri.EscapeDataString(status ?? string.Empty)}&priority={Uri.EscapeDataString(priority ?? string.Empty)}&q={Uri.EscapeDataString(q ?? string.Empty)}&mine={mine.ToString().ToLowerInvariant()}&page={page}&pageSize={pageSize}", ct);

        public Task<IReadOnlyList<SupportCategoryDto>> GetTicketCategoriesAsync(CancellationToken ct = default)
            => GetAsync<IReadOnlyList<SupportCategoryDto>>("api/tickets/categories", ct);

        public Task<TicketDto> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default)
            => PostAsync<TicketDto>("api/tickets", request, ct);

        public Task<TicketDetailDto> GetTicketAsync(Guid id, CancellationToken ct = default)
            => GetAsync<TicketDetailDto>($"api/tickets/{id}", ct);

        public Task<TicketMessageDto> AddTicketMessageAsync(Guid id, AddMessageRequest request, CancellationToken ct = default)
            => PostAsync<TicketMessageDto>($"api/tickets/{id}/messages", request, ct);

        public Task<TicketDto> UpdateTicketStatusAsync(Guid id, UpdateTicketStatusRequest request, CancellationToken ct = default)
            => PatchAsync<TicketDto>($"api/tickets/{id}/status", request, ct);

        public Task<TicketDto> AssignTicketAsync(Guid id, AssignTicketRequest request, CancellationToken ct = default)
            => PostAsync<TicketDto>($"api/tickets/{id}/assign", request, ct);

        // ---- Notifications ------------------------------------------------
        public Task<NotificationListResponse> GetNotificationsAsync(bool unreadOnly, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<NotificationListResponse>($"api/notifications?unread={unreadOnly.ToString().ToLowerInvariant()}&page={page}&pageSize={pageSize}", ct);

        public Task MarkNotificationReadAsync(Guid id, CancellationToken ct = default)
            => PostAsync<object>($"api/notifications/{id}/read", new { }, ct);

        public async Task<int> MarkAllNotificationsReadAsync(CancellationToken ct = default)
        {
            UpdatedCount result = await PostAsync<UpdatedCount>("api/notifications/read-all", new { }, ct);
            return result.Updated;
        }
    }

    public partial class BVipApiClient
    {
        // ---- Admin --------------------------------------------------------
        public Task<AdminOverviewDto> GetAdminOverviewAsync(CancellationToken ct = default)
            => GetAsync<AdminOverviewDto>("api/admin/overview", ct);

        public Task<SettingsResponse> GetSettingsAsync(CancellationToken ct = default)
            => GetAsync<SettingsResponse>("api/admin/settings", ct);

        public Task<SettingDto> UpdateSettingAsync(string key, string? value, CancellationToken ct = default)
            => PatchAsync<SettingDto>($"api/admin/settings/{Uri.EscapeDataString(key)}", new UpdateSettingRequest(value), ct);

        public Task<IReadOnlyList<IntegrationDto>> GetIntegrationsAsync(CancellationToken ct = default)
            => GetAsync<IReadOnlyList<IntegrationDto>>("api/admin/integrations", ct);

        public Task<IntegrationDto> CreateIntegrationAsync(CreateIntegrationRequest request, CancellationToken ct = default)
            => PostAsync<IntegrationDto>("api/admin/integrations", request, ct);

        public Task<IntegrationDto> UpdateIntegrationAsync(Guid id, UpdateIntegrationRequest request, CancellationToken ct = default)
            => PatchAsync<IntegrationDto>($"api/admin/integrations/{id}", request, ct);

        public Task<TestIntegrationResponse> TestIntegrationAsync(Guid id, CancellationToken ct = default)
            => PostAsync<TestIntegrationResponse>($"api/admin/integrations/{id}/test", new { }, ct);

        public Task<HealthResponse> GetAdminHealthAsync(CancellationToken ct = default)
            => GetAsync<HealthResponse>("api/admin/health", ct);

        public Task<SystemStatusDto> GetSystemStatusAsync(CancellationToken ct = default)
            => GetAsync<SystemStatusDto>("api/admin/status", ct);

        public Task<IReadOnlyList<ReleaseDto>> GetReleasesAsync(CancellationToken ct = default)
            => GetAsync<IReadOnlyList<ReleaseDto>>("api/admin/versions", ct);

        public Task<ReleaseDto> CreateReleaseAsync(CreateReleaseRequest request, CancellationToken ct = default)
            => PostAsync<ReleaseDto>("api/admin/versions", request, ct);

        public Task<IReadOnlyList<BackupDto>> GetBackupsAsync(CancellationToken ct = default)
            => GetAsync<IReadOnlyList<BackupDto>>("api/admin/backups", ct);

        public Task<BackupActionResponse> CreateBackupAsync(CreateBackupRequest request, CancellationToken ct = default)
            => PostAsync<BackupActionResponse>("api/admin/backups", request, ct);

        public Task<BackupActionResponse> RestoreBackupAsync(Guid id, CancellationToken ct = default)
            => PostAsync<BackupActionResponse>($"api/admin/backups/{id}/restore", new { Confirm = true }, ct);

        public Task<MaintenanceDto> GetMaintenanceAsync(CancellationToken ct = default)
            => GetAsync<MaintenanceDto>("api/admin/maintenance", ct);

        public Task<MaintenanceDto> SetMaintenanceAsync(UpdateMaintenanceRequest request, CancellationToken ct = default)
            => PatchAsync<MaintenanceDto>("api/admin/maintenance", request, ct);

        public Task<AuditEventListResponse> GetAuditEventsAsync(string? action, string? outcome, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize, CancellationToken ct = default)
            => GetAsync<AuditEventListResponse>(
                $"api/admin/audit-events?action={Uri.EscapeDataString(action ?? string.Empty)}&outcome={Uri.EscapeDataString(outcome ?? string.Empty)}" +
                $"&from={(fromUtc.HasValue ? Uri.EscapeDataString(fromUtc.Value.ToString("o")) : string.Empty)}" +
                $"&to={(toUtc.HasValue ? Uri.EscapeDataString(toUtc.Value.ToString("o")) : string.Empty)}" +
                $"&page={page}&pageSize={pageSize}", ct);

        // ---- Meta ----------------------------------------------------------
        public Task<MetaDto> GetMetaAsync(CancellationToken ct = default)
            => GetAsync<MetaDto>("api/meta", ct);

        /// <summary>Probes the unauthenticated health endpoint; never throws on a non-200 answer.</summary>
        public async Task<HealthProbe> ProbeHealthAsync(CancellationToken ct = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "api/health");
            HttpResponseMessage response = await _http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            return new HealthProbe(response.StatusCode, response.IsSuccessStatusCode, body);
        }

        private sealed record UpdatedCount(int Updated);
    }
}
