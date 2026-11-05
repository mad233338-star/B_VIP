using B_VIP.TestSupport;
using Xunit;

namespace B_VIP.ApiTests
{
    /// <summary>One backend host and one disposable database shared by every API test class.</summary>
    public sealed class ApiTestHost : BackendTestHost
    {
        public ApiTestHost() : base("b_vip_api_test")
        {
        }
    }

    [CollectionDefinition(Name)]
    public sealed class ApiCollection : ICollectionFixture<ApiTestHost>
    {
        public const string Name = "api";
    }
}
