using System.Text;
using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>Executes the ADB operations of the engine against the real adb executable.</summary>
    public sealed class AdbOperationExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;

        public AdbOperationExecutor(IAdbClient adb, IToolLocator tools)
        {
            _adb = adb;
            _tools = tools;
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.AdbDevices,
            OperationTypes.AdbReadInfo,
            OperationTypes.AdbProperties,
            OperationTypes.AdbConnectionCheck,
            OperationTypes.AdbRestartServer,
            OperationTypes.AdbCollectLogcat,
            OperationTypes.AdbReboot
        ];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (request.OperationType is OperationTypes.AdbReadInfo)
            {
                return await ReadInfoAsync(request, ct);
            }

            List<string> log = new();
            List<DiagnosticCheckDto> checks = new();
            List<string> warnings = new();
            List<string> errors = new();

            ProcessResult? result = request.OperationType switch
            {
                OperationTypes.AdbDevices => await RunAndLog(log, ["devices", "-l"], 30, ct),
                OperationTypes.AdbRestartServer => await RunAndLog(log, ["kill-server"], 30, ct),
                OperationTypes.AdbProperties => request.Serial is null ? null : await RunAndLog(log, ["-s", request.Serial!, "shell", "getprop"], 60, ct),
                OperationTypes.AdbConnectionCheck => request.Serial is null ? null : await RunAndLog(log, ["-s", request.Serial!, "get-state"], 30, ct),
                OperationTypes.AdbReboot => request.Serial is null ? null : await RunAndLog(log, RebootArguments(request), 60, ct),
                OperationTypes.AdbCollectLogcat => request.Serial is null ? null : await RunAndLog(log, ["-s", request.Serial!, "logcat", "-d", "-v", "time"], 90, ct),
                _ => null
            };

            if (result is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "The operation requires a connected device serial.",
                    "No device serial was supplied for this operation.");
            }

            if (!result.Succeeded)
            {
                errors.Add(result.FailureReason ?? result.StandardError.Trim());
            }

            switch (request.OperationType)
            {
                case OperationTypes.AdbDevices:
                    IReadOnlyList<DeviceInfoDto> devices = DeviceDetector.ParseAdbDevices(result.StandardOutput);
                    checks.Add(new DiagnosticCheckDto("devices-listed", devices.Count > 0 ? "pass" : "warn",
                        devices.Count > 0 ? $"{devices.Count} device(s) reported by adb." : "adb reported no attached devices.", null));
                    break;

                case OperationTypes.AdbConnectionCheck:
                    checks.Add(new DiagnosticCheckDto("adb-state", result.Succeeded ? "pass" : "fail",
                        result.Succeeded ? result.StandardOutput.Trim() : "The device did not report a usable adb state.", null));
                    break;

                case OperationTypes.AdbCollectLogcat:
                    checks.Add(new DiagnosticCheckDto("logcat-collected", result.Succeeded ? "pass" : "fail",
                        $"{result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} log line(s) captured.", null));
                    break;

                case OperationTypes.AdbReboot:
                    if (result.Succeeded)
                    {
                        string target = request.GetParameter("target") ?? "system";
                        warnings.Add($"Reboot to {target} was requested; the device may disappear from the list for a few seconds.");
                    }

                    break;
            }

            if (request.OperationType is OperationTypes.AdbRestartServer && result.Succeeded)
            {
                warnings.Add("The adb server was stopped. Start it again by scanning for devices.");
            }

            bool succeeded = result.Succeeded;
            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                BuildSummary(request, succeeded, checks, errors),
                checks, log, warnings, errors);
        }

        /// <summary>Maps the requested reboot target onto the adb command that reaches it.</summary>
        private static string[] RebootArguments(OperationExecutionRequest request)
        {
            List<string> args = ["-s", request.Serial!, "reboot"];
            string? target = request.GetParameter("target");
            if (!string.IsNullOrWhiteSpace(target) && !target.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                args.Add(target.Trim().ToLowerInvariant());
            }

            return args.ToArray();
        }

        /// <summary>
        /// Reads the device twice: <c>adb devices -l</c> for the transport facts and
        /// <c>getprop</c> for the build identity, then prints a readable report next to the raw lines.
        /// </summary>
        private async Task<OperationExecutionResult> ReadInfoAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "Detailed read requires a connected device serial.",
                    "No device serial was supplied for this operation.");
            }

            List<string> log = new();
            List<string> errors = new();
            List<string> warnings = new();
            List<DiagnosticCheckDto> checks = new();

            ProcessResult devices = await RunAndLog(log, ["devices", "-l"], 30, ct);
            if (!devices.Succeeded)
            {
                errors.Add(devices.FailureReason ?? devices.StandardError.Trim());
            }

            ProcessResult properties = await RunAndLog(log, ["-s", request.Serial, "shell", "getprop"], 60, ct);
            if (!properties.Succeeded)
            {
                errors.Add(properties.FailureReason ?? properties.StandardError.Trim());
                return new OperationExecutionResult(request.OperationType, false, OperationOutcome.Failed,
                    errors.FirstOrDefault() ?? "getprop failed.", checks, log, warnings, errors);
            }

            Dictionary<string, string> values = ParseProperties(properties.StandardOutput);

            (string Label, string Key)[] wanted =
            [
                ("Manufacturer", "ro.product.manufacturer"),
                ("Model", "ro.product.model"),
                ("Brand", "ro.product.brand"),
                ("Device", "ro.product.device"),
                ("Android version", "ro.build.version.release"),
                ("SDK", "ro.build.version.sdk"),
                ("Security patch", "ro.build.version.security_patch"),
                ("Build", "ro.build.display.id"),
                ("Fingerprint", "ro.build.fingerprint"),
                ("Bootloader", "ro.bootloader"),
                ("Serial", "ro.serialno"),
                ("CPU ABI", "ro.product.cpu.abi")
            ];

            log.Add(string.Empty);
            log.Add("=== Device information ===");
            int reported = 0;
            foreach ((string label, string key) in wanted)
            {
                string value = values.TryGetValue(key, out string? found) ? found : string.Empty;
                log.Add($"{label,-18}: {(string.IsNullOrWhiteSpace(value) ? "—" : value)}");

                if (!string.IsNullOrWhiteSpace(value))
                {
                    reported++;
                    checks.Add(new DiagnosticCheckDto(key, "pass", value, null));
                }
            }

            string model = values.TryGetValue("ro.product.model", out string? m) ? m : "device";
            string release = values.TryGetValue("ro.build.version.release", out string? r) ? r : "unknown";
            string sdk = values.TryGetValue("ro.build.version.sdk", out string? s) ? s : "?";

            bool succeeded = reported > 0 && errors.Count == 0;
            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                succeeded ? $"{model} — Android {release} (SDK {sdk}), {reported} propert{(reported == 1 ? "y" : "ies")} read."
                          : errors.FirstOrDefault() ?? "No property was returned by the device.",
                checks, log, warnings, errors);
        }

        /// <summary>Parses the <c>[key]: [value]</c> format printed by <c>getprop</c>.</summary>
        internal static Dictionary<string, string> ParseProperties(string output)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int open = line.IndexOf('[');
                int close = line.IndexOf(']');
                if (open != 0 || close <= 1)
                {
                    continue;
                }

                string name = line[1..close];
                if (name.Length == 0)
                {
                    continue;
                }

                int valueOpen = line.IndexOf('[', close);
                int valueClose = line.LastIndexOf(']');
                if (valueOpen < 0 || valueClose <= valueOpen)
                {
                    continue;
                }

                values[name] = line[(valueOpen + 1)..valueClose];
            }

            return values;
        }

        private async Task<ProcessResult> RunAndLog(List<string> log, string[] args, int timeout, CancellationToken ct)
        {
            ProcessResult result = await _adb.AdbAsync(args, timeout, ct);
            log.Add($"$ adb {string.Join(' ', args)}");
            log.AddRange(AppendOutput(result));
            return result;
        }

        internal static IEnumerable<string> AppendOutput(ProcessResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return line;
                }
            }

            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                yield return "stderr: " + result.StandardError.Trim();
            }

            if (result.TimedOut)
            {
                yield return "The tool exceeded its timeout and was terminated.";
            }
        }

        private static string BuildSummary(OperationExecutionRequest request, bool succeeded,
            IReadOnlyList<DiagnosticCheckDto> checks, IReadOnlyList<string> errors)
        {
            if (!succeeded)
            {
                return errors.Count > 0 ? errors[0] : "The adb command failed.";
            }

            return checks.Count > 0 ? checks[0].Detail ?? "Completed." : "Completed.";
        }
    }
}
