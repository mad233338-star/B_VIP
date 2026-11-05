using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Licenses;

namespace B_VIP.Admin
{
    /// <summary>
    /// One row of the technician table: the directory entry plus the license that is currently
    /// attached to it. Everything is projected from server data; nothing is invented locally.
    /// </summary>
    public sealed class TechnicianRow : AdminObject
    {
        private LicensePlanDto? _planChoice;

        public TechnicianRow(UserSummaryDto user, LicenseDto? license)
        {
            User = user;
            License = license;
        }

        /// <summary>The tier the administrator picked in this row before pressing Move.</summary>
        public LicensePlanDto? PlanChoice
        {
            get => _planChoice;
            set => SetProperty(ref _planChoice, value);
        }

        public UserSummaryDto User { get; }

        public LicenseDto? License { get; }

        public Guid Id => User.Id;

        public string UserName => User.UserName;

        public string Email => User.Email;

        public string Status => User.Status;

        public string VerificationText => User.EmailVerified ? "Verified" : "Pending";

        public string HardwareId => string.IsNullOrWhiteSpace(User.HardwareId) ? "—" : User.HardwareId;

        public string HardwareResetText => User.HardwareIdResetCount == 0 ? "0" : User.HardwareIdResetCount.ToString();

        public string Tier => License?.PlanName ?? "—";

        public string ExpiryText => License?.ExpiresAt is null
            ? (License is null ? "—" : "Lifetime")
            : License.ExpiresAt.Value.ToString("yyyy-MM-dd");

        public string RegisteredText => User.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd");

        public bool IsSuspended => string.Equals(User.Status, "Suspended", StringComparison.OrdinalIgnoreCase);

        public string StatusAction => IsSuspended ? "Activate" : "Suspend";

        public string VerifyAction => User.EmailVerified ? "Mark unverified" : "Verify e-mail";
    }
}
