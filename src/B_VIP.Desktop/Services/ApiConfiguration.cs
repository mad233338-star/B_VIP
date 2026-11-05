namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// The single place the desktop client learns where its cloud service lives.
    ///
    /// The production endpoint is compiled in, so a technician cannot be tricked into typing a
    /// look-alike server address into a sign-in box (the field is gone from the UI). The address is
    /// never written to the live console: the log only says whether the service answered.
    ///
    /// Two deliberate escape hatches exist for development and never in a release payload:
    ///   * <c>B_VIP_API_URL</c> in the environment points the client at a local backend;
    ///   * an <c>Api:BaseUrl</c> entry in the shipped appsettings.json overrides it for a test build.
    /// </summary>
    public static class ApiConfiguration
    {
        /// <summary>The production backend every copy of the tool talks to.</summary>
        public const string ProductionBaseUrl = "https://b-vip.onrender.com";

        /// <summary>Environment variable that overrides the built-in endpoint (development only).</summary>
        public const string BaseUrlOverrideVariable = "B_VIP_API_URL";

        /// <summary>
        /// Resolves the endpoint: environment override, then the shipped appsettings value, then the
        /// compiled production address. Only absolute http/https addresses are accepted.
        /// </summary>
        public static string ResolveBaseUrl(string? shippedValue = null)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(BaseUrlOverrideVariable);
            if (IsUsable(fromEnvironment))
            {
                return Normalize(fromEnvironment!);
            }

            if (IsUsable(shippedValue))
            {
                return Normalize(shippedValue!);
            }

            return ProductionBaseUrl;
        }

        /// <summary>True when the client talks to the compiled production service.</summary>
        public static bool IsProduction(string baseUrl)
            => string.Equals(Normalize(baseUrl), ProductionBaseUrl, StringComparison.OrdinalIgnoreCase);

        /// <summary>The host shown in the status bar, never a full route.</summary>
        public static string Describe(string baseUrl)
            => Uri.TryCreate(Normalize(baseUrl), UriKind.Absolute, out Uri? uri) ? uri.Host : "unknown service";

        private static bool IsUsable(string? value)
            => !string.IsNullOrWhiteSpace(value) &&
               Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static string Normalize(string value) => value.Trim().TrimEnd('/');
    }
}
