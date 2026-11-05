using B_VIP.Core.Rules;
using Xunit;

namespace B_VIP.UnitTests
{
    public sealed class SubscriptionTierTests
    {
        [Fact]
        public void The_free_trial_lasts_three_days()
        {
            DateTime activated = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

            Assert.Equal(3, LicenseRules.TrialDays);
            Assert.Equal("trial", LicenseRules.TrialPlanCode);
            Assert.Equal(activated.AddHours(72), LicenseRules.TrialExpiry(activated));
        }

        [Theory]
        [InlineData("monthly", "1 Month", 30, 2, false, 9.99)]
        [InlineData("semi-annual", "6 Months", 180, 3, false, 39.99)]
        [InlineData("annual", "12 Months", 365, 5, false, 69.99)]
        [InlineData("lifetime", "Lifetime", null, 10, true, 199.00)]
        public void A_well_formed_tier_is_accepted(string code, string name, int? days, int maxActivations, bool lifetime, decimal price)
        {
            (string normalized, string? error) = LicenseRules.ValidatePlan(code, name, days, maxActivations, lifetime, price);

            Assert.Null(error);
            Assert.Equal(code, normalized);
        }

        [Fact]
        public void The_code_is_normalized_to_lower_case()
        {
            (string normalized, string? error) = LicenseRules.ValidatePlan("PRO-ANNUAL", "Pro Annual", 365, 3, false, 10);

            Assert.Null(error);
            Assert.Equal("pro-annual", normalized);
        }

        [Theory]
        [InlineData("", "Plan", 30, 1, false, null)]
        [InlineData("x", "Plan", 30, 1, false, null)]
        [InlineData("has spaces", "Plan", 30, 1, false, null)]
        [InlineData("monthly", "", 30, 1, false, null)]
        [InlineData("monthly", "Plan", null, 1, false, null)]
        [InlineData("monthly", "Plan", 0, 1, false, null)]
        [InlineData("monthly", "Plan", 4000, 1, false, null)]
        [InlineData("monthly", "Plan", 30, 0, false, null)]
        [InlineData("monthly", "Plan", 30, 5000, false, null)]
        public void An_invalid_tier_is_rejected_with_a_reason(
            string code, string name, int? days, int maxActivations, bool lifetime, decimal? price)
        {
            (_, string? error) = LicenseRules.ValidatePlan(code, name, days, maxActivations, lifetime, price);

            Assert.NotNull(error);
        }

        [Fact]
        public void A_negative_price_is_rejected()
        {
            (_, string? error) = LicenseRules.ValidatePlan("monthly", "Plan", 30, 1, lifetime: false, price: -1m);

            Assert.NotNull(error);
        }

        [Fact]
        public void A_lifetime_tier_needs_no_duration()
        {
            (_, string? error) = LicenseRules.ValidatePlan("lifetime", "Lifetime", null, 1, true, null);

            Assert.Null(error);
        }

        [Fact]
        public void A_trial_expiry_equals_a_plan_computed_expiry_for_the_same_days()
        {
            DateTime start = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.Equal(
                LicenseRules.ComputeExpiry(start, LicenseRules.TrialDays, lifetime: false),
                LicenseRules.TrialExpiry(start));
        }
    }
}
