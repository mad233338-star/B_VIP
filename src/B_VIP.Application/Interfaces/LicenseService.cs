using B_VIP.Contracts.Common;
using B_VIP.Core.Common;
using B_VIP.Contracts.Licenses;

namespace B_VIP.Application.Interfaces
{
    /// <summary>Licensing: plans, licenses, verification and device activation.</summary>
    public interface ILicenseService
    {
        Task<IReadOnlyList<LicensePlanDto>> GetPlansAsync(CancellationToken ct = default);

        /// <summary>Adds a subscription tier the plans screen can sell.</summary>
        Task<LicensePlanDto> CreatePlanAsync(CreatePlanRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        /// <summary>Edits a tier; returns null when the tier does not exist.</summary>
        Task<LicensePlanDto?> UpdatePlanAsync(Guid id, UpdatePlanRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        /// <summary>Deletes an unused tier; refuses (409) while licenses still reference it.</summary>
        Task<bool> DeletePlanAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        /// <summary>Moves the technician's current license onto another tier.</summary>
        Task<LicenseDto?> ChangePlanAsync(Guid userId, Guid planId, bool resetPeriod, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        /// <summary>Grants the three-day trial once, right after the address is verified. Idempotent.</summary>
        Task<LicenseDto?> EnsureTrialAsync(Guid userId, string? correlationId, CancellationToken ct = default);

        Task<PagedResult<LicenseDto>> SearchLicensesAsync(string? query, string? status, Guid? userId, int page, int pageSize, CancellationToken ct = default);

        Task<LicenseDto?> GetLicenseAsync(Guid id, CancellationToken ct = default);

        Task<LicenseDto> CreateLicenseAsync(CreateLicenseRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<LicenseDto?> UpdateLicenseAsync(Guid id, UpdateLicenseRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<LicenseDto?> SuspendLicenseAsync(Guid id, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<LicenseDto?> ReactivateLicenseAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<LicenseDto?> RevokeLicenseAsync(Guid id, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<LicenseActivationListResponse?> GetActivationsAsync(Guid id, CancellationToken ct = default);

        Task<bool> DeactivateActivationAsync(Guid licenseId, Guid activationId, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct = default);

        Task<VerifyLicenseResponse> VerifyAsync(VerifyLicenseRequest request, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<ActivateLicenseResponse> ActivateAsync(ActivateLicenseRequest request, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<bool> DeactivateAsync(DeactivateLicenseRequest request, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default);

        Task<IReadOnlyList<LicenseVerificationEventDto>> GetVerificationEventsAsync(Guid licenseId, int limit, CancellationToken ct = default);

        /// <summary>Returns the license that is currently valid for a user, if any.</summary>
        Task<Contracts.Auth.LicenseSummaryDto?> GetSummaryForUserAsync(Guid userId, CancellationToken ct = default);
    }
}
