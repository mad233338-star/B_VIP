using B_VIP.Infrastructure.Services;

namespace B_VIP.Backend.Services
{
    /// <summary>Binds the host environment name for the infrastructure layer.</summary>
    public sealed class HostEnvironmentAccessor : IWebHostEnvironmentAccessor
    {
        private readonly IHostEnvironment _environment;

        public HostEnvironmentAccessor(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public string EnvironmentName => _environment.EnvironmentName;
    }
}
