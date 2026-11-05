using B_VIP.Hardware;

namespace B_VIP.Modules.Samsung
{
    /// <summary>A browser target the operator can push to the handset screen.</summary>
    public sealed record BrowserTarget(string Key, string Label, string Url, string? Package)
    {
        public static readonly BrowserTarget YouTube =
            new("youtube", "YouTube", "https://www.youtube.com", "com.google.android.youtube");

        public static readonly BrowserTarget Chrome =
            new("chrome", "Google Chrome", "https://www.google.com", "com.android.chrome");

        public static readonly BrowserTarget SamsungInternet =
            new("samsung-internet", "Samsung Internet", "https://www.google.com", "com.sec.android.app.sbrowser");

        public static BrowserTarget Custom(string url) => new("custom", "Custom URL", url, null);

        public static IReadOnlyList<BrowserTarget> Presets { get; } = [YouTube, Chrome, SamsungInternet];
    }

    public sealed record BrowserOpenResult(bool Succeeded, string Target, string? Command, string Detail);

    /// <summary>
    /// Opens a URL on the connected handset.
    ///
    /// There is no MTP message that makes a phone launch a browser: MTP transports files and object
    /// properties only, and a phone in MTP mode cannot be told to run an intent. The route that does
    /// work is the Android intent dispatcher over an authorized ADB connection, which is what this
    /// service drives; when ADB is not available the call fails with that explanation instead of
    /// pretending a payload was accepted.
    /// </summary>
    public sealed class MtpBrowserService
    {
        private readonly IAdbClient _adb;

        public MtpBrowserService(IAdbClient adb)
        {
            _adb = adb;
        }

        public async Task<BrowserOpenResult> OpenAsync(string? serial, BrowserTarget target, CancellationToken ct = default)
        {
            if (serial is null)
            {
                return new BrowserOpenResult(false, target.Label, null,
                    "No authorized ADB device is connected. MTP itself cannot dispatch a URL to the handset.");
            }

            if (!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return new BrowserOpenResult(false, target.Label, null,
                    $"'{target.Url}' is not an absolute http/https address.");
            }

            if (uri.OriginalString.Any(char.IsWhiteSpace))
            {
                return new BrowserOpenResult(false, target.Label, null,
                    "The URL contains whitespace; only a single-token address can be passed through adb shell.");
            }

            List<string> args = ["-s", serial, "shell", "am", "start",
                "-a", "android.intent.action.VIEW",
                "-d", uri.AbsoluteUri];

            if (target.Package is not null)
            {
                args.Add("-p");
                args.Add(target.Package);
            }

            ProcessResult result = await _adb.AdbAsync(args, 30, ct);
            string command = $"adb {string.Join(' ', args)}";

            if (!result.Succeeded)
            {
                string reason = result.FailureReason ?? result.StandardError.Trim();
                if (reason.Contains("Error: Activity not started", StringComparison.OrdinalIgnoreCase) ||
                    reason.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                {
                    reason += $" The {target.Label} package may not be installed on this handset.";
                }

                return new BrowserOpenResult(false, target.Label, command, reason);
            }

            return new BrowserOpenResult(true, target.Label, command,
                $"{target.Label} was asked to open {uri.AbsoluteUri}.");
        }
    }
}
