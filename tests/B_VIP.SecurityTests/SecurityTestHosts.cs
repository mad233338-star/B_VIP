using B_VIP.TestSupport;
using Xunit;

namespace B_VIP.SecurityTests
{
    /// <summary>Standard host with relaxed rate limits, used by the authorization and isolation tests.</summary>
    public sealed class SecurityTestHost : BackendTestHost
    {
        public SecurityTestHost() : base("b_vip_security_test")
        {
        }
    }

    /// <summary>Host that keeps the production rate limits (10 auth calls per minute) active.</summary>
    public sealed class StrictRateLimitHost : BackendTestHost
    {
        public StrictRateLimitHost() : base("b_vip_security_strict", strictRateLimits: true)
        {
        }
    }

    [CollectionDefinition(Name)]
    public sealed class SecurityCollection :
        ICollectionFixture<SecurityTestHost>,
        ICollectionFixture<StrictRateLimitHost>
    {
        public const string Name = "security";
    }
}
