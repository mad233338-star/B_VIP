namespace B_VIP.Contracts.Licenses
{
    public sealed record LicensePlanDto(
        Guid Id,
        string Code,
        string Name,
        string? NameAr,
        string Description,
        int? DurationDays,
        int MaxActivations,
        bool IsLifetime,
        bool IsActive,
        int SortOrder,
        int LicenseCount,
        decimal? Price = null,
        string? Currency = null);

    /// <summary>Creates a subscription tier the technician plans screen can sell.</summary>
    public sealed record CreatePlanRequest(
        string Code,
        string Name,
        string? NameAr,
        string Description,
        int? DurationDays,
        int MaxActivations = 1,
        decimal? Price = null,
        string? Currency = "USD",
        bool IsLifetime = false,
        int SortOrder = 0,
        bool IsActive = true);

    /// <summary>Edits a tier; every field is optional so a single control can be changed.</summary>
    public sealed record UpdatePlanRequest(
        string? Name = null,
        string? NameAr = null,
        string? Description = null,
        int? DurationDays = null,
        int? MaxActivations = null,
        decimal? Price = null,
        string? Currency = null,
        bool? IsLifetime = null,
        int? SortOrder = null,
        bool? IsActive = null);

    /// <summary>Reassigns the technician's current license to another tier.</summary>
    public sealed record ChangePlanRequest(Guid PlanId, bool ResetPeriod = true);

    public sealed record LicenseDto(
        Guid Id,
        string LicenseKey,
        Guid UserId,
        string UserName,
        Guid PlanId,
        string PlanCode,
        string PlanName,
        string Status,
        string EffectiveStatus,
        DateTime StartsAt,
        DateTime? ExpiresAt,
        int MaxActivations,
        int CurrentActivations,
        DateTime CreatedUtc,
        DateTime? LastVerifiedUtc,
        string Source,
        string? AdminNotes,
        IReadOnlyList<string> Features,
        IReadOnlyList<ActivationDto> Activations);

    public sealed record ActivationDto(
        Guid Id,
        string DeviceFingerprint,
        string DeviceName,
        string? Platform,
        string? AppVersion,
        DateTime ActivatedUtc,
        DateTime? LastSeenUtc,
        DateTime? DeactivatedUtc);

    public sealed record CreateLicenseRequest(
        Guid UserId,
        string PlanCode,
        DateTime? StartsAt = null,
        int? MaxActivations = null,
        DateTime? ExpiresAt = null,
        string? Notes = null);

    public sealed record UpdateLicenseRequest(
        DateTime? StartsAt,
        DateTime? ExpiresAt,
        int? MaxActivations,
        string? Notes,
        IReadOnlyList<string>? Features);

    public sealed record LicenseActionRequest(string? Reason);

    public sealed record VerifyLicenseRequest(
        string? LicenseKey,
        string? DeviceFingerprint,
        string? ClientVersion,
        bool RecordEvent = true);

    public sealed record VerifyLicenseResponse(
        string Result,
        string? LicenseKey,
        string? PlanCode,
        DateTime? StartsAt,
        DateTime? ExpiresAt,
        int MaxActivations,
        int CurrentActivations,
        IReadOnlyList<string> Features,
        string Message,
        DateTime ServerTimeUtc);

    public sealed record ActivateLicenseRequest(
        string LicenseKey,
        string DeviceFingerprint,
        string DeviceName,
        string? Platform = null,
        string? AppVersion = null);

    public sealed record ActivateLicenseResponse(
        bool Success,
        string ActivationId,
        string Message,
        int RemainingActivations);

    public sealed record DeactivateLicenseRequest(string DeviceFingerprint, string? Reason = null);

    public sealed record LicenseVerificationEventDto(
        Guid Id,
        Guid LicenseId,
        DateTime PerformedUtc,
        string Result,
        string? Detail,
        string? IpAddress,
        string? ClientVersion);

    public sealed record LicenseActivationListResponse(
        IReadOnlyList<ActivationDto> Activations,
        int MaxActivations);
}
