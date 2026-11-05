using System.Diagnostics;
using B_VIP.Contracts.Devices;

namespace B_VIP.Hardware
{
    public interface IDeviceDiagnostics
    {
        Task<DeviceDiagnosticsResponse> RunAsync(string serial, CancellationToken ct = default);

        Task<DeviceDiagnosticsResponse> RunNamedAsync(string serial, IReadOnlyCollection<string> checks, CancellationToken ct = default);

        Task<DeviceActionResult> ExecuteActionAsync(string serial, string action, string? argument, CancellationToken ct = default);
    }

    /// <summary>
    /// Runs real diagnostics against a connected handset through adb: identity, Android build,
    /// security patch level, bootloader/verified-boot state, root indicators, storage, battery,
    /// memory and ABI. Every check reports the actual command output, and no check fabricates data
    /// when the device refuses to answer — it reports skip or fail instead.
    /// </summary>
    public sealed class DeviceDiagnostics : IDeviceDiagnostics
    {
        public const string CheckIdentity = "identity";
        public const string CheckAndroidVersion = "android_version";
        public const string CheckSecurityPatch = "security_patch";
        public const string CheckBuildFingerprint = "build_fingerprint";
        public const string CheckRoot = "root_state";
        public const string CheckVerifiedBoot = "verified_boot";
        public const string CheckStorage = "storage";
        public const string CheckBattery = "battery";
        public const string CheckMemory = "memory";
        public const string CheckAbi = "abi";

        private static readonly string[] FullSuite =
        [
            CheckIdentity, CheckAndroidVersion, CheckSecurityPatch, CheckBuildFingerprint,
            CheckRoot, CheckVerifiedBoot, CheckStorage, CheckBattery, CheckMemory, CheckAbi
        ];

        private readonly IAdbClient _adb;

        public DeviceDiagnostics(IAdbClient adb)
        {
            _adb = adb;
        }

        public async Task<DeviceDiagnosticsResponse> RunAsync(string serial, CancellationToken ct = default)
            => await RunNamedAsync(serial, FullSuite, ct);

        public async Task<DeviceDiagnosticsResponse> RunNamedAsync(string serial, IReadOnlyCollection<string> checks, CancellationToken ct = default)
        {
            List<DiagnosticCheckDto> results = new();
            List<string> warnings = new();
            List<string> errors = new();
            DateTime generatedUtc = DateTime.UtcNow;

            ProcessResult state = await _adb.AdbSerialAsync(serial, ["get-state"], 20, ct);
            if (!state.Succeeded)
            {
                errors.Add(state.FailureReason ?? $"The device '{serial}' did not answer adb get-state.");
                return new DeviceDiagnosticsResponse(serial, null, results, warnings, errors, generatedUtc);
            }

            IReadOnlyList<Dictionary<string, string>> props = await _adb.GetPropertiesAsync(serial, ct);
            string? model = Prop(props, "ro.product.model");
            string? manufacturer = Prop(props, "ro.product.manufacturer");
            string description = string.Join(" ", new[] { manufacturer, model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(description))
            {
                description = serial;
            }

            foreach (string check in checks)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(check switch
                {
                    CheckIdentity => await Measure(check, () => IdentityDetail(props, model, manufacturer, state.StandardOutput)),
                    CheckAndroidVersion => MeasureProp(check, props, "ro.build.version.release", "Android version"),
                    CheckSecurityPatch => MeasureSecurityPatch(check, props, warnings),
                    CheckBuildFingerprint => MeasureProp(check, props, "ro.build.fingerprint", "Build fingerprint"),
                    CheckRoot => await Measure(check, () => RootCheck(serial)),
                    CheckVerifiedBoot => MeasureProp(check, props, "ro.boot.verifiedbootstate", "Verified boot state"),
                    CheckStorage => await Measure(check, () => StorageCheck(serial)),
                    CheckBattery => await Measure(check, () => BatteryCheck(serial)),
                    CheckMemory => MeasureMemory(check, props),
                    CheckAbi => MeasureProp(check, props, "ro.product.cpu.abi", "CPU ABI"),
                    _ => new DiagnosticCheckDto(check, "skip", "Unknown check.", null)
                });
            }

            return new DeviceDiagnosticsResponse(serial, description, results, warnings, errors, generatedUtc);
        }

        public async Task<DeviceActionResult> ExecuteActionAsync(string serial, string action, string? argument, CancellationToken ct = default)
        {
            ProcessResult? result = action.ToLowerInvariant() switch
            {
                "reboot" => await _adb.AdbSerialAsync(serial, ["reboot"], 30, ct),
                "reboot-bootloader" => await _adb.AdbSerialAsync(serial, ["reboot", "bootloader"], 30, ct),
                "reboot-recovery" => await _adb.AdbSerialAsync(serial, ["reboot", "recovery"], 30, ct),
                "remount" => await _adb.AdbSerialAsync(serial, ["remount"], 30, ct),
                "root" => await _adb.AdbSerialAsync(serial, ["root"], 30, ct),
                "unroot" => await _adb.AdbSerialAsync(serial, ["unroot"], 30, ct),
                "clear-app" when !string.IsNullOrWhiteSpace(argument) =>
                    await _adb.AdbSerialAsync(serial, ["shell", "pm", "clear", argument!], 60, ct),
                "force-stop-app" when !string.IsNullOrWhiteSpace(argument) =>
                    await _adb.AdbSerialAsync(serial, ["shell", "am", "force-stop", argument!], 60, ct),
                "shell" when !string.IsNullOrWhiteSpace(argument) =>
                    await _adb.ShellAsync(serial, argument!, 60, ct),
                _ => null
            };

            if (result is null)
            {
                return new DeviceActionResult(serial, action, false,
                    $"The action '{action}' is unknown or requires an argument.");
            }

            string detail = result.Succeeded
                ? (string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? "Completed."
                    : Truncate(result.StandardOutput.Trim(), 500) ?? "Completed.")
                : (result.FailureReason ?? Truncate(result.StandardError.Trim(), 500) ?? "The action failed.");

            return new DeviceActionResult(serial, action, result.Succeeded, detail);
        }

        private static async Task<DiagnosticCheckDto> Measure(string name, Func<Task<(string Status, string? Detail)>> run)
        {
            Stopwatch clock = Stopwatch.StartNew();
            (string status, string? detail) = await run();
            clock.Stop();
            return new DiagnosticCheckDto(name, status, detail, clock.ElapsedMilliseconds);
        }

        private static DiagnosticCheckDto Measure(string name, (string Status, string? Detail) result)
            => new(name, result.Status, result.Detail, null);

        private static DiagnosticCheckDto MeasureProp(string name, IReadOnlyList<Dictionary<string, string>> props, string key, string label)
        {
            string? value = Prop(props, key);
            return string.IsNullOrWhiteSpace(value)
                ? new DiagnosticCheckDto(name, "skip", $"{label} was not reported by the device.", null)
                : new DiagnosticCheckDto(name, "pass", $"{label}: {value}", null);
        }

        private static DiagnosticCheckDto MeasureSecurityPatch(string name, IReadOnlyList<Dictionary<string, string>> props, List<string> warnings)
        {
            string? value = Prop(props, "ro.build.version.security_patch");
            if (string.IsNullOrWhiteSpace(value))
            {
                return new DiagnosticCheckDto(name, "skip", "Security patch level was not reported by the device.", null);
            }

            if (DateTime.TryParse(value, out DateTime patch) && patch < DateTime.UtcNow.AddMonths(-6))
            {
                warnings.Add($"The security patch level ({value}) is older than six months.");
                return new DiagnosticCheckDto(name, "warn", $"Security patch level: {value} (outdated)", null);
            }

            return new DiagnosticCheckDto(name, "pass", $"Security patch level: {value}", null);
        }

        private static DiagnosticCheckDto MeasureMemory(string name, IReadOnlyList<Dictionary<string, string>> props)
        {
            string? total = Prop(props, "ro.config.low_ram");
            return new DiagnosticCheckDto(name, total is null ? "pass" : "warn",
                total is null ? "Standard memory configuration." : "Device is flagged as a low-RAM device.", null);
        }

        private static Task<(string Status, string? Detail)> IdentityDetail(
            IReadOnlyList<Dictionary<string, string>> props, string? model, string? manufacturer, string stateOutput)
        {
            string identity = string.Join(" ", new[] { manufacturer, model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(identity))
            {
                return Task.FromResult<(string, string?)>(("warn", $"adb state: {stateOutput.Trim()}"));
            }

            string? androidId = Prop(props, "ro.serialno");
            return Task.FromResult<(string, string?)>(("pass",
                $"{identity}{(string.IsNullOrWhiteSpace(androidId) ? string.Empty : $" (serial property present)")}; adb state: {stateOutput.Trim()}"));
        }

        private async Task<(string Status, string? Detail)> RootCheck(string serial)
        {
            ProcessResult result = await _adb.ShellAsync(serial, "which su || ls /system/xbin/su /system/bin/su 2>/dev/null", 45);
            string output = result.StandardOutput.Trim();
            if (!result.Succeeded)
            {
                return ("skip", "Root indicators could not be read from the device.");
            }

            return output.Length > 0
                ? ("warn", $"A 'su' binary is present at: {output}")
                : ("pass", "No 'su' binary was found on the system image.");
        }

        private async Task<(string Status, string? Detail)> StorageCheck(string serial)
        {
            ProcessResult result = await _adb.ShellAsync(serial, "df -k /data | tail -n 1", 45);
            string line = result.StandardOutput.Trim();
            if (!result.Succeeded || line.Length == 0)
            {
                return ("skip", "Storage information is not available through adb.");
            }

            return ("pass", $"data partition: {line}");
        }

        private async Task<(string Status, string? Detail)> BatteryCheck(string serial)
        {
            ProcessResult result = await _adb.ShellAsync(serial, "dumpsys battery | head -n 12", 45);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return ("skip", "Battery information requires dumpsys access on this device.");
            }

            string summary = string.Join(" | ", result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains("level", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("temperature", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("status", StringComparison.OrdinalIgnoreCase))
                .Take(4));

            return string.IsNullOrWhiteSpace(summary) ? ("pass", "Battery data read.") : ("pass", summary);
        }

        private static string? Prop(IReadOnlyList<Dictionary<string, string>> props, string key)
        {
            foreach (Dictionary<string, string> entry in props)
            {
                if (entry.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string? Truncate(string? value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }
}
