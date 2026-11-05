using B_VIP.Core;
using B_VIP.Core.Entities;
using B_VIP.Core.Rules;
using Xunit;

namespace B_VIP.UnitTests
{
    public sealed class LicenseRulesTests
    {
        private static License ActiveLicense(DateTime? expiresAt = null, int maxActivations = 2, LicenseStatus status = LicenseStatus.Active)
            => new()
            {
                Id = Guid.NewGuid(),
                LicenseKey = LicenseRules.GenerateLicenseKey(),
                Status = status,
                StartsAt = DateTime.UtcNow.AddDays(-1),
                ExpiresAt = expiresAt,
                MaxActivations = maxActivations
            };

        [Fact]
        public void Effective_status_resolves_an_active_license()
        {
            License license = ActiveLicense();
            Assert.Equal(LicenseStatus.Active, LicenseRules.EffectiveStatus(license, DateTime.UtcNow));
            Assert.True(LicenseRules.IsActive(license, DateTime.UtcNow));
        }

        [Fact]
        public void Active_license_past_its_expiry_resolves_to_expired()
        {
            License license = ActiveLicense(DateTime.UtcNow.AddMinutes(-1));

            Assert.Equal(LicenseStatus.Expired, LicenseRules.EffectiveStatus(license, DateTime.UtcNow));
            Assert.False(LicenseRules.IsActive(license, DateTime.UtcNow));
            Assert.True(LicenseRules.IsPastExpiry(license, DateTime.UtcNow));
        }

        [Fact]
        public void Active_license_before_its_expiry_is_not_expired()
        {
            License license = ActiveLicense(DateTime.UtcNow.AddDays(30));

            Assert.Equal(LicenseStatus.Active, LicenseRules.EffectiveStatus(license, DateTime.UtcNow));
            Assert.False(LicenseRules.IsPastExpiry(license, DateTime.UtcNow));
        }

        [Fact]
        public void Terminal_and_inactive_statuses_pass_through_unchanged()
        {
            Assert.Equal(LicenseStatus.Revoked, LicenseRules.EffectiveStatus(ActiveLicense(status: LicenseStatus.Revoked), DateTime.UtcNow));
            Assert.Equal(LicenseStatus.Suspended, LicenseRules.EffectiveStatus(ActiveLicense(status: LicenseStatus.Suspended), DateTime.UtcNow));
            Assert.Equal(LicenseStatus.Pending, LicenseRules.EffectiveStatus(ActiveLicense(status: LicenseStatus.Pending), DateTime.UtcNow));
            Assert.Equal(LicenseStatus.Invalid, LicenseRules.EffectiveStatus(ActiveLicense(status: LicenseStatus.Invalid), DateTime.UtcNow));
            Assert.Equal(LicenseStatus.Invalid, LicenseRules.EffectiveStatus(ActiveLicense(status: (LicenseStatus)99), DateTime.UtcNow));
        }

        [Fact]
        public void Expired_status_stays_expired_even_before_the_date()
        {
            License license = ActiveLicense(DateTime.UtcNow.AddDays(10), status: LicenseStatus.Expired);
            Assert.Equal(LicenseStatus.Expired, LicenseRules.EffectiveStatus(license, DateTime.UtcNow));
        }

        [Fact]
        public void CanActivate_allows_an_active_license_with_free_slots()
        {
            License license = ActiveLicense(maxActivations: 2);

            Assert.True(LicenseRules.CanActivate(license, DateTime.UtcNow, out string? reason));
            Assert.Null(reason);
        }

        [Fact]
        public void CanActivate_blocks_when_the_activation_limit_is_reached()
        {
            License license = ActiveLicense(maxActivations: 1);
            license.Activations.Add(new LicenseActivation { Id = Guid.NewGuid(), DeviceFingerprint = "fp1", ActivatedUtc = DateTime.UtcNow });

            Assert.False(LicenseRules.CanActivate(license, DateTime.UtcNow, out string? reason));
            Assert.Equal("activation-limit-reached", reason);
        }

        [Fact]
        public void CanActivate_ignores_deactivated_devices_when_counting()
        {
            License license = ActiveLicense(maxActivations: 1);
            license.Activations.Add(new LicenseActivation
            {
                Id = Guid.NewGuid(),
                DeviceFingerprint = "fp1",
                ActivatedUtc = DateTime.UtcNow,
                DeactivatedUtc = DateTime.UtcNow
            });

            Assert.True(LicenseRules.CanActivate(license, DateTime.UtcNow, out _));
        }

        [Theory]
        [InlineData(LicenseStatus.Revoked, "license-revoked")]
        [InlineData(LicenseStatus.Suspended, "license-suspended")]
        [InlineData(LicenseStatus.Pending, "license-pending")]
        public void CanActivate_blocks_inactive_statuses_with_a_reason(LicenseStatus status, string expectedReason)
        {
            License license = ActiveLicense(status: status);

            Assert.False(LicenseRules.CanActivate(license, DateTime.UtcNow, out string? reason));
            Assert.Equal(expectedReason, reason);
        }

        [Fact]
        public void CanActivate_blocks_an_expired_license()
        {
            License license = ActiveLicense(DateTime.UtcNow.AddMinutes(-5));

            Assert.False(LicenseRules.CanActivate(license, DateTime.UtcNow, out string? reason));
            Assert.Equal("license-expired", reason);
        }

        [Fact]
        public void ComputeExpiry_follows_the_plan_duration()
        {
            DateTime startsAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.Null(LicenseRules.ComputeExpiry(startsAt, null, lifetime: true));
            Assert.Null(LicenseRules.ComputeExpiry(startsAt, 30, lifetime: true));
            Assert.Null(LicenseRules.ComputeExpiry(startsAt, null, lifetime: false));
            Assert.Null(LicenseRules.ComputeExpiry(startsAt, 0, lifetime: false));
            Assert.Null(LicenseRules.ComputeExpiry(startsAt, -5, lifetime: false));
            Assert.Equal(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc), LicenseRules.ComputeExpiry(startsAt, 30, lifetime: false));
        }

        [Fact]
        public void GenerateLicenseKey_produces_the_BVIP_dashed_format()
        {
            string key = LicenseRules.GenerateLicenseKey();

            Assert.StartsWith("BVIP-", key);
            Assert.Equal(19, key.Length);
            Assert.Equal('-', key[4]);
            Assert.Equal('-', key[9]);
            Assert.Equal('-', key[14]);

            string body = key[5..];
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            foreach (char c in body.Replace("-", string.Empty))
            {
                Assert.Contains(c, alphabet);
            }

            // The body deliberately excludes visually ambiguous characters.
            Assert.DoesNotContain('I', body);
            Assert.DoesNotContain('O', body);
            Assert.DoesNotContain('0', body);
            Assert.DoesNotContain('1', body);
        }

        [Fact]
        public void GenerateLicenseKey_does_not_repeat_immediately()
        {
            HashSet<string> keys = new();
            for (int i = 0; i < 200; i++)
            {
                keys.Add(LicenseRules.GenerateLicenseKey());
            }

            Assert.Equal(200, keys.Count);
        }

        [Fact]
        public void IsFeatureEnabled_reads_the_entitlements_case_insensitively()
        {
            License license = ActiveLicense();
            license.Entitlements.Add(new LicenseFeatureEntitlement { FeatureKey = "Advanced Reports", Enabled = true });
            license.Entitlements.Add(new LicenseFeatureEntitlement { FeatureKey = "Cloud Sync", Enabled = false });

            Assert.True(LicenseRules.IsFeatureEnabled(license, "Advanced Reports"));
            Assert.True(LicenseRules.IsFeatureEnabled(license, "ADVANCED REPORTS"));
            Assert.False(LicenseRules.IsFeatureEnabled(license, "Cloud Sync"));
            Assert.False(LicenseRules.IsFeatureEnabled(license, "Unknown Feature"));
        }

        [Fact]
        public void ToVerificationResult_maps_every_status()
        {
            Assert.Equal(VerificationResult.Valid, LicenseRules.ToVerificationResult(LicenseStatus.Active));
            Assert.Equal(VerificationResult.Expired, LicenseRules.ToVerificationResult(LicenseStatus.Expired));
            Assert.Equal(VerificationResult.Suspended, LicenseRules.ToVerificationResult(LicenseStatus.Suspended));
            Assert.Equal(VerificationResult.Revoked, LicenseRules.ToVerificationResult(LicenseStatus.Revoked));
            Assert.Equal(VerificationResult.InvalidRequest, LicenseRules.ToVerificationResult(LicenseStatus.Invalid));
            Assert.Equal(VerificationResult.Pending, LicenseRules.ToVerificationResult(LicenseStatus.Pending));
        }
    }
}
