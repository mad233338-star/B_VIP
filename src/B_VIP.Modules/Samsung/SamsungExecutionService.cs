using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.Samsung
{
    /// <summary>
    /// Samsung routines: reading a handset over MTP, rebooting it, and the test-mode/ADB FRP flow.
    ///
    /// Every step is reported through the operation observer so the shell can stream it into the live
    /// console. Nothing is simulated: when a step cannot run (no MTP device, no modem port, no
    /// authorized ADB connection) the result says exactly which step stopped and what to check.
    /// </summary>
    public sealed class SamsungExecutionService
    {
        private const int DefaultAuthorizationWaitSeconds = 60;

        private readonly IAdbClient _adb;
        private readonly IMtpProtocolService _mtp;
        private readonly MtpBrowserService _browser;

        public SamsungExecutionService(IAdbClient adb, IMtpProtocolService mtp, MtpBrowserService browser)
        {
            _adb = adb;
            _mtp = mtp;
            _browser = browser;
        }

        /// <summary>
        /// Reads the identity of the handset. Windows publishes model and manufacturer over MTP with no
        /// USB debugging; serial, firmware, CSC and Android version only exist on an authorized ADB
        /// connection, so each field records which source produced it.
        /// </summary>
        public async Task<OperationExecutionResult> ReadInfoAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<string> log = new();
            List<DiagnosticCheckDto> checks = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Info("Step 1/4 — enumerating MTP portable devices.");
            IReadOnlyList<MtpDeviceInfo> mtpDevices;
            try
            {
                // A provider that returns nothing at all must read as "no MTP device", not as a
                // null-reference crash in the middle of the screen's read.
                mtpDevices = await _mtp.FindDevicesAsync(ct) ?? Array.Empty<MtpDeviceInfo>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mtpDevices = Array.Empty<MtpDeviceInfo>();
                warnings.Add($"The MTP device list could not be read: {ex.Message}");
            }

            if (mtpDevices.Count == 0)
            {
                warnings.Add("Windows reports no MTP portable device. Connect the handset with the screen unlocked and 'Transferring files' selected.");
                log.Add("MTP devices: none");
            }
            else
            {
                MtpDeviceInfo? samsung = mtpDevices.FirstOrDefault(d => d.LooksSamsung) ?? mtpDevices.FirstOrDefault();
                log.Add($"MTP devices: {string.Join(", ", mtpDevices.Select(d => d.Summary))}");

                if (samsung is not null)
                {
                    checks.Add(new DiagnosticCheckDto("mtp-device", "pass", $"MTP: {samsung.Summary}", null));
                    checks.Add(new DiagnosticCheckDto("mtp-model", samsung.Model is null ? "skip" : "pass",
                        samsung.Model ?? "Windows did not publish a model name.", null));
                    checks.Add(new DiagnosticCheckDto("mtp-manufacturer", samsung.Manufacturer is null ? "skip" : "pass",
                        samsung.Manufacturer ?? "Windows did not publish a manufacturer.", null));

                    if (!samsung.LooksSamsung)
                    {
                        warnings.Add("The MTP device does not identify itself as Samsung; the Samsung-only routines may refuse it.");
                    }
                }
            }

            request.Info("Step 2/4 — looking for an authorized ADB connection.");
            string? serial = request.Serial;
            IReadOnlyList<DeviceInfoDto> devices = await ScanAdbAsync(ct);
            serial ??= devices.FirstOrDefault(d => d.State is DeviceConnectionState.Connected or DeviceConnectionState.Recovery)?.Serial;

            if (serial is null)
            {
                string state = devices.FirstOrDefault()?.State.ToString() ?? "no adb device";
                checks.Add(new DiagnosticCheckDto("adb-connection", "skip", $"Not authorized for adb ({state}).", null));
                warnings.Add("Serial number, firmware, CSC and Android version are not part of MTP; enable USB debugging and accept the RSA prompt to read them.");
                log.Add("ADB: no authorized device");
            }
            else
            {
                request.Info($"Step 3/4 — reading getprop from {serial}.");
                checks.Add(new DiagnosticCheckDto("adb-connection", "pass", $"Authorized adb device: {serial}", null));

                // getprop output is whatever the handset prints: a truncated line, a property with no
                // brackets or a connection that drops mid-read must not abort the whole screen. The
                // properties that did arrive are still reported.
                IReadOnlyList<Dictionary<string, string>> props;
                try
                {
                    props = await _adb.GetPropertiesAsync(serial, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warnings.Add($"The property list could not be read: {ex.Message}");
                    props = Array.Empty<Dictionary<string, string>>();
                }

                props ??= Array.Empty<Dictionary<string, string>>();

                Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, string> entry in props)
                {
                    if (entry is null)
                    {
                        continue;
                    }

                    foreach ((string key, string value) in entry)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            values[key] = value ?? string.Empty;
                        }
                    }
                }

                (string Label, string Key)[] wanted =
                [
                    ("Serial number", "ro.serialno"),
                    ("Firmware", "ro.build.PDA"),
                    ("Build", "ro.build.display.id"),
                    ("CSC / sales code", "ro.csc.sales_code"),
                    ("Product code", "ro.product.name"),
                    ("Android version", "ro.build.version.release"),
                    ("Security patch", "ro.build.version.security_patch"),
                    ("Unique ID", "ro.boot.serialno"),
                    ("Knox warranty bit", "ro.boot.warranty_bit")
                ];

                int found = 0;
                log.Add("=== Samsung device information ===");
                foreach ((string label, string key) in wanted)
                {
                    string? value = Resolve(values, key);
                    log.Add($"{label,-20}: {(value is null ? "—" : value)}");
                    if (value is not null)
                    {
                        found++;
                        checks.Add(new DiagnosticCheckDto(key, "pass", $"{label}: {value}", null));
                    }
                    else
                    {
                        checks.Add(new DiagnosticCheckDto(key, "skip", $"{label} was not reported.", null));
                    }
                }

                if (found == 0)
                {
                    errors.Add("The device answered adb but returned no property; the connection may have dropped.");
                }
            }

            request.Info("Step 4/4 — report complete.");
            log.AddRange(checks.Select(c => $"[{c.Status.ToUpperInvariant()}] {c.Name}: {c.Detail}"));

            bool succeeded = errors.Count == 0 && checks.Any(c => c.Status == "pass");
            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                succeeded
                    ? $"Samsung information read ({checks.Count(c => c.Status == "pass")} field(s))."
                    : errors.FirstOrDefault() ?? "No Samsung information could be read.",
                checks, log, warnings, errors);
        }

        /// <summary>
        /// Reboots the handset into Download (Odin) mode or back to Android. The command travels over
        /// adb because MTP has no reboot verb; the mode switch itself is the device's own.
        /// </summary>
        public async Task<OperationExecutionResult> RebootAsync(OperationExecutionRequest request, string target, CancellationToken ct)
        {
            List<string> log = new();
            List<DiagnosticCheckDto> checks = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Info($"Reboot requested: {target}.");

            string? serial = request.Serial;
            if (serial is null)
            {
                IReadOnlyList<DeviceInfoDto> devices = await ScanAdbAsync(ct);
                serial = devices.FirstOrDefault(d => d.State is DeviceConnectionState.Connected or DeviceConnectionState.Recovery)?.Serial;
            }

            if (serial is null)
            {
                errors.Add("No authorized ADB device is connected. MTP has no reboot command: enable USB debugging, or use the hardware key combination for Download mode.");
                checks.Add(new DiagnosticCheckDto("adb-connection", "fail", "No authorized adb device.", null));
                return VendorSupport.Build(request.OperationType, errors[0], checks, log, warnings, errors);
            }

            List<string> args = ["-s", serial, "reboot"];
            if (!string.Equals(target, "system", StringComparison.OrdinalIgnoreCase))
            {
                args.Add(target.ToLowerInvariant());
            }

            request.Process($"$ adb {string.Join(' ', args)}");
            log.Add($"$ adb {string.Join(' ', args)}");

            ProcessResult result = await _adb.AdbAsync(args, 60, ct);
            log.AddRange(AdbOperationExecutor.AppendOutput(result));

            checks.Add(new DiagnosticCheckDto("reboot", result.Succeeded ? "pass" : "fail",
                result.Succeeded ? $"Reboot to {target} was accepted; the device leaves the list for a few seconds." : result.FailureReason ?? result.StandardError.Trim(),
                result.DurationMs));

            if (!result.Succeeded)
            {
                errors.Add(result.FailureReason ?? result.StandardError.Trim());
            }

            warnings.Add("If the handset reports the reboot but stays in Android, the bootloader refused the mode switch; use the key combination instead.");

            return VendorSupport.Build(request.OperationType,
                result.Succeeded ? $"Reboot to {target} sent." : "The reboot was refused.", checks, log, warnings, errors);
        }

        public Task<BrowserOpenResult> OpenBrowserAsync(string? serial, BrowserTarget target, CancellationToken ct)
            => _browser.OpenAsync(serial, target, ct);

        /// <summary>
        /// Test-mode FRP flow: handshake the Samsung modem over the USB serial interface, read the
        /// software version, then wait for an authorized ADB connection and clear the setup flags.
        ///
        /// The modem has no command that switches USB debugging on or approves the RSA prompt — that
        /// authorisation is granted on the device screen — so the routine waits for it and says so
        /// instead of pretending the AT sequence did it.
        /// </summary>
        public async Task<OperationExecutionResult> ResetFrpTestModeAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<string> log = new();
            List<DiagnosticCheckDto> checks = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                return OperationExecutionResult.Skipped(request.OperationType,
                    "The FRP reset was not confirmed.",
                    "The operator did not confirm the FRP reset, so nothing was executed.");
            }

            // 1. Modem handshake.
            request.Info("Step 1/6 — locating the Samsung modem COM port.");
            IReadOnlyList<SamsungModemPort> ports = OperatingSystem.IsWindows()
                ? SamsungModemLocator.FindPorts()
                : Array.Empty<SamsungModemPort>();

            checks.Add(new DiagnosticCheckDto("modem-port", ports.Count > 0 ? "pass" : "warn",
                ports.Count > 0 ? string.Join(", ", ports.Select(p => p.Summary)) : "No Samsung modem/serial COM port was found.",
                null));

            if (ports.Count == 0)
            {
                warnings.Add("No Samsung COM port: install the Samsung USB driver and reconnect the handset in MTP mode. The routine continues with adb if a device is already authorized.");
            }
            else
            {
                request.Info($"Step 2/6 — opening {ports[0].PortName} at {SamsungModemSession.BaudRate} baud.");
                AtCommandResult? handshake = null;

                foreach (SamsungModemPort port in ports.Take(2))
                {
                    try
                    {
                        using SamsungModemSession session = SamsungModemSession.Open(port.PortName);
                        log.Add($"opened {port.PortName} ({port.FriendlyName})");

                        handshake = await session.SendAsync("AT", ct);
                        log.Add($"AT -> {Describe(handshake)}");

                        if (!handshake.Succeeded)
                        {
                            warnings.Add($"{port.PortName} did not answer AT; trying the next port.");
                            continue;
                        }

                        request.Success($"Modem handshake OK on {port.PortName}.");
                        AtCommandResult devInfo = await session.SendAsync("AT+DEVCONINFO", ct);
                        log.Add($"AT+DEVCONINFO -> {Describe(devInfo)}");
                        checks.Add(new DiagnosticCheckDto("AT+DEVCONINFO", devInfo.Succeeded ? "pass" : "warn",
                            VendorSupport.Truncate(devInfo.Response, 400), devInfo.DurationMs));

                        AtCommandResult swVersion = await session.SendAsync("AT+SWVERSION", ct);
                        log.Add($"AT+SWVERSION -> {Describe(swVersion)}");
                        checks.Add(new DiagnosticCheckDto("AT+SWVERSION", swVersion.Succeeded ? "pass" : "warn",
                            VendorSupport.Truncate(swVersion.Response, 400), swVersion.DurationMs));
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                    {
                        warnings.Add($"Could not open or use {port.PortName}: {ex.Message}");
                        request.Warning($"{port.PortName}: {ex.Message}");
                    }
                }

                if (handshake is not { Succeeded: true })
                {
                    warnings.Add("The modem never answered AT. Another program (Odin, Smart Switch, a terminal) may hold the port.");
                }
            }

            // 2. ADB authorisation.
            int waitSeconds = int.TryParse(request.GetParameter("waitSeconds"), out int parsed) && parsed >= 0
                ? parsed
                : DefaultAuthorizationWaitSeconds;

            request.Info($"Step 3/6 — waiting up to {waitSeconds}s for an authorized ADB connection.");
            string? serial = request.Serial;
            string state = "no adb device";
            DateTime deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
            bool unauthorizedSeen = false;

            do
            {
                IReadOnlyList<DeviceInfoDto> devices = await ScanAdbAsync(ct);
                DeviceInfoDto? candidate = devices.FirstOrDefault(d => d.State is DeviceConnectionState.Connected or DeviceConnectionState.Recovery);
                if (candidate is not null)
                {
                    serial = candidate.Serial;
                    state = candidate.State.ToString();
                    break;
                }

                DeviceInfoDto? pending = devices.FirstOrDefault(d => d.State == DeviceConnectionState.Unauthorized);
                if (pending is not null)
                {
                    state = "unauthorized";
                    if (!unauthorizedSeen)
                    {
                        unauthorizedSeen = true;
                        request.Warning("The device is connected but not authorized: accept the 'Allow USB debugging' prompt on the handset.");
                        warnings.Add("An RSA prompt is waiting on the device screen; it must be accepted before the FRP flags can be cleared.");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            while (DateTime.UtcNow < deadline && ct.IsCancellationRequested == false);

            if (serial is null)
            {
                checks.Add(new DiagnosticCheckDto("adb-connection", "fail", $"No authorized adb device ({state}).", null));
                errors.Add("No authorized ADB connection. The modem sequence cannot enable USB debugging by itself — enable it on the handset (or in test mode via the service menu) and accept the RSA prompt, then run the routine again.");
                return VendorSupport.Build(request.OperationType,
                    "FRP reset stopped: the device is not authorized for adb.", checks, log, warnings, errors);
            }

            request.Success($"ADB authorized on {serial}.");
            checks.Add(new DiagnosticCheckDto("adb-connection", "pass", $"Authorized adb device: {serial}", null));

            // 3. FRP flags over adb.
            (string Label, string[] Shell)[] commands =
            [
                ("user_setup_complete", ["settings", "put", "secure", "user_setup_complete", "1"]),
                ("device_provisioned", ["settings", "put", "global", "device_provisioned", "1"]),
                ("content-insert", ["content", "insert", "--uri", "content://settings/secure",
                    "--bind", "name:s:user_setup_complete", "--bind", "value:s:1"]),
                ("clear setup wizard", ["pm", "clear", "com.google.android.setupwizard"]),
                ("clear gsf", ["pm", "clear", "com.google.android.gsf"]),
                ("enable setup wizard", ["pm", "enable", "com.google.android.setupwizard"]),
                ("reboot", ["reboot"])
            ];

            int step = 4;
            foreach ((string label, string[] shell) in commands)
            {
                request.Info($"Step {step}/6 — {label}.");
                log.Add($"$ adb shell {string.Join(' ', shell)}");
                ProcessResult result = await _adb.AdbAsync(["-s", serial, "shell", .. shell], 60, ct);
                log.AddRange(AdbOperationExecutor.AppendOutput(result));

                checks.Add(new DiagnosticCheckDto(label, result.Succeeded ? "pass" : "fail",
                    result.Succeeded ? "accepted" : result.FailureReason ?? result.StandardError.Trim(),
                    result.DurationMs));

                if (!result.Succeeded)
                {
                    errors.Add($"{label}: {result.FailureReason ?? result.StandardError.Trim()}");
                    request.Error($"{label} failed.");
                    if (label is "reboot")
                    {
                        continue;
                    }

                    break;
                }

                step++;
            }

            bool succeeded = errors.Count == 0;
            request.Info("FRP routine finished.");

            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                succeeded
                    ? "FRP flags cleared and the device rebooted. Complete the setup wizard without signing in."
                    : errors.FirstOrDefault() ?? "The FRP reset did not complete.",
                checks, log, warnings, errors);
        }

        private async Task<IReadOnlyList<DeviceInfoDto>> ScanAdbAsync(CancellationToken ct)
        {
            ProcessResult result = await _adb.AdbAsync(["devices", "-l"], 20, ct);
            return result.Succeeded ? DeviceDetector.ParseAdbDevices(result.StandardOutput) : Array.Empty<DeviceInfoDto>();
        }

        private static string? Resolve(Dictionary<string, string> values, string key)
        {
            if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return null;
        }

        private static string Describe(AtCommandResult result)
        {
            string body = string.IsNullOrWhiteSpace(result.Response) ? "(no answer)" : result.Response.Replace("\r", string.Empty).Replace("\n", " | ");
            return result.Succeeded ? body : $"{body} [{result.FailureReason}]";
        }
    }
}
