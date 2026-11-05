using B_VIP.TestSupport;
using Xunit;

namespace B_VIP.IntegrationTests
{
    /// <summary>One backend host and one disposable database shared by every integration test class.</summary>
    public sealed class IntegrationTestHost : BackendTestHost
    {
        public IntegrationTestHost() : base("b_vip_integration_test")
        {
        }
    }

    [CollectionDefinition(Name)]
    public sealed class IntegrationCollection : ICollectionFixture<IntegrationTestHost>
    {
        public const string Name = "integration";
    }
}
