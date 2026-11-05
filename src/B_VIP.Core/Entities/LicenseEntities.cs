namespace B_VIP.Core.Entities
{
    /// <summary>Commercial plan (Trial, Monthly, Quarterly, Annual, Lifetime, Custom...).</summary>
    public class LicensePlan
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string Description { get; set; } = string.Empty;
        public int? DurationDays { get; set; }
        public int MaxActivations { get; set; } = 1;
        public decimal? Price { get; set; }
        public string? Currency { get; set; }
        public bool IsLifetime { get; set; }
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public ICollection<License> Licenses { get; set; } = new List<License>();
    }

    public class License
    {
        public Guid Id { get; set; }
        public string LicenseKey { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public Guid LicensePlanId { get; set; }
        public LicensePlan LicensePlan { get; set; } = null!;
        public LicenseStatus Status { get; set; } = LicenseStatus.Pending;
        public DateTime StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public int MaxActivations { get; set; } = 1;
        public int CurrentActivations { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? LastVerifiedUtc { get; set; }
        public DateTime? SuspendedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? SuspensionReason { get; set; }
        public string? CreatedByUserId { get; set; }
        public string? AdminNotes { get; set; }
        public string Source { get; set; } = "admin";

        public ICollection<LicenseActivation> Activations { get; set; } = new List<LicenseActivation>();
        public ICollection<LicenseVerificationEvent> VerificationEvents { get; set; } = new List<LicenseVerificationEvent>();
        public ICollection<LicenseFeatureEntitlement> Entitlements { get; set; } = new List<LicenseFeatureEntitlement>();
    }

    public class LicenseActivation
    {
        public Guid Id { get; set; }
        public Guid LicenseId { get; set; }
        public License License { get; set; } = null!;
        public string DeviceFingerprint { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string? Platform { get; set; }
        public string? AppVersion { get; set; }
        public string? IpAddress { get; set; }
        public DateTime ActivatedUtc { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public DateTime? DeactivatedUtc { get; set; }
        public string? DeactivatedReason { get; set; }
    }

    public class LicenseVerificationEvent
    {
        public Guid Id { get; set; }
        public Guid LicenseId { get; set; }
        public License License { get; set; } = null!;
        public Guid? UserId { get; set; }
        public DateTime PerformedUtc { get; set; }
        public VerificationResult Result { get; set; }
        public string? Detail { get; set; }
        public string? IpAddress { get; set; }
        public string? CorrelationId { get; set; }
        public string? ClientVersion { get; set; }
    }

    public class LicenseFeatureEntitlement
    {
        public Guid Id { get; set; }
        public Guid LicenseId { get; set; }
        public License License { get; set; } = null!;
        public string FeatureKey { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string? Value { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
