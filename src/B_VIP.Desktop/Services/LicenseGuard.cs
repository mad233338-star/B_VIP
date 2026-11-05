using B_VIP.Contracts.Auth;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Client-side gate in front of the hardware screens. The server still refuses a lapsed license
    /// on every operation; this exists so the technician is told *why* nothing ran and is taken to
    /// the renewal screen instead of watching a command fail.
    ///
    /// A 3-day trial is an ordinary active license here: trial and paid access behave identically
    /// until the expiry date passes.
    /// </summary>
    public static class LicenseGuard
    {
        /// <summary>True while device work is allowed. <paramref name="reason"/> explains a refusal.</summary>
        public static bool CanRunOperations(LicenseSummaryDto? license, DateTime utcNow, out string reason)
        {
            if (license is null)
            {
                reason = "No subscription is attached to this account. Open License & Activation to choose a plan.";
                return false;
            }

            if (license.ExpiresAt.HasValue && license.ExpiresAt.Value <= utcNow)
            {
                reason = $"The {license.PlanName} subscription ended on {license.ExpiresAt.Value.ToLocalTime():yyyy-MM-dd}. Renew it to run device operations.";
                return false;
            }

            if (!string.Equals(license.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                reason = license.Status switch
                {
                    "Suspended" => "This subscription is suspended. Contact the administrator.",
                    "Revoked" => "This subscription was revoked. Contact the administrator.",
                    "Expired" => "This subscription has expired. Renew it to run device operations.",
                    _ => $"The subscription is '{license.Status}', so device operations are locked."
                };
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static bool CanRunOperations(LicenseSummaryDto? license, out string reason)
            => CanRunOperations(license, DateTime.UtcNow, out reason);

        /// <summary>Human countdown used by the dashboard and the license screen.</summary>
        public static string DescribeRemaining(LicenseSummaryDto? license, DateTime utcNow)
        {
            if (license is null)
            {
                return "No active subscription";
            }

            if (license.ExpiresAt is null)
            {
                return "Lifetime access";
            }

            TimeSpan left = license.ExpiresAt.Value - utcNow;
            if (left <= TimeSpan.Zero)
            {
                return "Expired";
            }

            int days = (int)left.TotalDays;
            int hours = left.Hours;
            return days > 0
                ? $"{days} day(s) and {hours} hour(s) remaining"
                : $"{hours} hour(s) and {left.Minutes} minute(s) remaining";
        }
    }
}
