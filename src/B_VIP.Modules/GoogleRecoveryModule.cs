using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Modules;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>
    /// Google account recovery guidance and lock diagnostics. An account lock is removed only
    /// through Google's official recovery flow; this module reads the state the handset really
    /// reports and opens the official pages on the device, it never bypasses a lock.
    /// </summary>
    public sealed class GoogleRecoveryExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;

        public GoogleRecoveryExecutor(IAdbClient adb)
        {
            _adb = adb;
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.GoogleRecoveryGuidance,
            OperationTypes.GoogleLockStatus,
            OperationTypes.GoogleOpenDeviceUrl
        ];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (string.Equals(request.OperationType, OperationTypes.GoogleLockStatus, StringComparison.OrdinalIgnoreCase))
            {
                return await LockStatusAsync(request, ct);
            }

            if (string.Equals(request.OperationType, OperationTypes.GoogleOpenDeviceUrl, StringComparison.OrdinalIgnoreCase))
            {
                return await OpenOnDeviceAsync(request, ct);
            }

            List<DiagnosticCheckDto> checks =
            [
                new("official-recovery-link", "pass", OfficialLinks.GoogleAccountRecovery, null),
                new("android-support-link", "pass", OfficialLinks.AndroidSupport, null),
                new("accounts-support-link", "pass", OfficialLinks.GoogleAccountsSupport, null)
            ];

            List<string> log =
            [
                "Google account recovery module executed.",
                "No lock is bypassed: the user is guided to the official Google account recovery flow."
            ];

            return new OperationExecutionResult(OperationTypes.GoogleRecoveryGuidance, true,
                OperationOutcome.Succeeded, "Official Google account recovery guidance was produced.",
                checks, log,
                new[] { "If the device is still locked after recovery, contact the device owner's mobile provider or Google Support." },
                Array.Empty<string>());
        }

        /// <summary>Reads FRP, OEM unlock, verified boot and Knox flags through adb getprop.</summary>
        private async Task<OperationExecutionResult> LockStatusAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required.", "No device serial was supplied. Connect the handset with USB debugging enabled.");
            }

            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            IReadOnlyList<Dictionary<string, string>> props = await VendorSupport.PropertiesAsync(_adb, request.Serial, ct);
            if (props.Count == 0)
            {
                errors.Add("The handset did not return any properties through adb; check the connection and the authorisation prompt.");
                return VendorSupport.Build(request.OperationType, "No data read.", checks, log, warnings, errors);
            }

            string? android = VendorSupport.Get(props, "ro.build.version.release");
            string? patch = VendorSupport.Get(props, "ro.build.version.security_patch");
            checks.Add(new DiagnosticCheckDto("android-version", android is null ? "skip" : "pass",
                android is null ? "Android version was not reported." : $"Android version: {android}", null));
            checks.Add(new DiagnosticCheckDto("security-patch", patch is null ? "skip" : "pass",
                patch is null ? "Security patch level was not reported." : $"Security patch level: {patch}", null));

            string? frp = VendorSupport.Any(props, "ro.frp.pst", "ro.frp");
            bool frpPresent = frp is not null;
            checks.Add(new DiagnosticCheckDto("frp-partition", frpPresent ? "warn" : "pass",
                frpPresent
                    ? $"A factory reset protection partition is configured ({VendorSupport.Truncate(frp!, 80)})."
                    : "No FRP partition property was reported by this build.",
                null));
            if (frpPresent)
            {
                warnings.Add("Factory reset protection is active; the owner must complete Google's official recovery flow.");
            }

            string? oemUnlock = VendorSupport.Any(props, "ro.oem_unlock_supported", "sys.oem_unlock_allowed");
            checks.Add(new DiagnosticCheckDto("oem-unlock", oemUnlock is null ? "skip" : "pass",
                oemUnlock is null ? "OEM unlock support was not reported." : $"OEM unlock supported: {oemUnlock}", null));

            string? verifiedBoot = VendorSupport.Any(props, "ro.boot.verifiedbootstate", "ro.boot.vbmeta.device_state");
            bool unlockedBootloader = string.Equals(verifiedBoot, "orange", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(verifiedBoot, "unlocked", StringComparison.OrdinalIgnoreCase);
            checks.Add(new DiagnosticCheckDto("verified-boot", verifiedBoot is null ? "skip" : unlockedBootloader ? "warn" : "pass",
                verifiedBoot is null ? "Verified boot state was not reported." : $"Verified boot state: {verifiedBoot}", null));

            string? knox = VendorSupport.Any(props, "ro.boot.warranty_bit", "ro.warranty_bit", "ro.boot.knox_status");
            string knoxStatus = knox is null ? "skip" : knox.Trim() == "0" ? "pass" : "warn";
            checks.Add(new DiagnosticCheckDto("knox-flags", knoxStatus,
                knox is null ? "No Knox/warranty flag was reported by this handset." : $"Knox/warranty flag: {knox}", null));

            string? secure = VendorSupport.Any(props, "ro.secure", "ro.debuggable");
            checks.Add(new DiagnosticCheckDto("build-hardening", secure is null ? "skip" : "pass",
                secure is null ? "Build hardening flags were not reported." : $"Build hardening (ro.secure/ro.debuggable): {secure}", null));

            log.Add("Lock status read through adb getprop (FRP, OEM unlock, verified boot, Knox).");
            warnings.Add("This module reports lock state only. Account recovery happens exclusively in Google's official flow.");

            return VendorSupport.Build(request.OperationType,
                frpPresent ? "The handset reports an active FRP partition." : "Lock status read; no FRP partition was reported.",
                checks, log, warnings, errors);
        }

        /// <summary>Opens the official recovery page on the handset display through an adb VIEW intent.</summary>
        private async Task<OperationExecutionResult> OpenOnDeviceAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> errors = new();

            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required.", "No device serial was supplied.");
            }

            string url = request.Parameters.TryGetValue("url", out string? supplied) && !string.IsNullOrWhiteSpace(supplied)
                ? supplied
                : OfficialLinks.GoogleAccountRecovery;

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "Invalid address.", "Only absolute http(s) addresses can be opened on the handset.");
            }

            ProcessResult result = await _adb.AdbSerialAsync(request.Serial,
                ["shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", uri.ToString()],
                timeoutSeconds: 30, ct);

            checks.Add(new DiagnosticCheckDto("open-url", result.Succeeded ? "pass" : "fail",
                result.Succeeded
                    ? $"The handset was asked to open {uri}."
                    : VendorSupport.Truncate(result.StandardError, 200),
                null));

            log.Add($"adb shell am start -a android.intent.action.VIEW -d {uri} -> exit {result.ExitCode?.ToString() ?? "?"}");

            if (result.Succeeded)
            {
                log.Add("Look at the handset display: the page opens in the device browser.");
            }
            else
            {
                errors.Add("The handset did not accept the browser intent. Unlock the screen and retry.");
            }

            return VendorSupport.Build(request.OperationType,
                result.Succeeded ? "Official page opened on the handset." : "The page could not be opened on the handset.",
                checks, log, new List<string>(), errors);
        }
    }
}
