using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>Executes Fastboot operations, including the destructive ones which require an explicit confirmation.</summary>
    public sealed class FastbootOperationExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;

        public FastbootOperationExecutor(IAdbClient adb, IToolLocator tools)
        {
            _adb = adb;
            _tools = tools;
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.FastbootDevices,
            OperationTypes.FastbootReadInfo,
            OperationTypes.FastbootResetFrp,
            OperationTypes.FastbootGetVar,
            OperationTypes.FastbootReboot,
            OperationTypes.FastbootFlash,
            OperationTypes.FastbootWipe,
            OperationTypes.FastbootEraseBatch
        ];

        /// <summary>
        /// Partitions that carry radio calibration and cannot be recovered once erased. The vendor
        /// tabs pass partition lists from their own workflows, so the refusal lives here rather
        /// than in a view model: no request can wipe these by accident.
        /// </summary>
        internal static readonly string[] ProtectedPartitions =
        [
            "modemst1", "modemst2", "fsg", "persist", "persistent",
            "nvram", "nvdata", "nvcfg", "prodnv", "protect1", "protect2",
            "boot", "recovery", "vbmeta", "bootloader", "preloader", "dtbo"
        ];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_tools.Find(ToolLocator.Fastboot)))
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "Fastboot is not installed.",
                    "The fastboot executable was not found. Install Android platform-tools first.");
            }

            // These two report a formatted result instead of a single command outcome.
            switch (request.OperationType)
            {
                case OperationTypes.FastbootReadInfo:
                    return await ReadInfoAsync(request, ct);

                case OperationTypes.FastbootResetFrp when request.Serial is null:
                    return OperationExecutionResult.Failure(request.OperationType,
                        "A device serial is required to reset FRP.",
                        "No device serial was supplied for this operation.");

                case OperationTypes.FastbootResetFrp:
                    return await ResetFrpAsync(request, ct);

                case OperationTypes.FastbootEraseBatch:
                    return await EraseBatchAsync(request, ct);
            }

            List<string> log = new();
            List<DiagnosticCheckDto> checks = new();
            List<string> warnings = new();
            List<string> errors = new();

            ProcessResult? result = request.OperationType switch
            {
                OperationTypes.FastbootDevices => await Run(log, ["devices"], 30, ct),
                OperationTypes.FastbootGetVar when request.Serial is not null =>
                    await Run(log, ["-s", request.Serial!, "getvar", request.GetParameter("variable") ?? "version"], 30, ct),
                OperationTypes.FastbootGetVar => await Run(log, ["getvar", request.GetParameter("variable") ?? "version"], 30, ct),
                OperationTypes.FastbootReboot => await Run(log, RebootArguments(request), 60, ct),
                OperationTypes.FastbootFlash => await FlashAsync(request, log, warnings, ct),
                OperationTypes.FastbootWipe => await WipeAsync(request, log, warnings, ct),
                _ => null
            };

            if (result is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "The operation could not be started.", "Missing device serial or required parameters.");
            }

            if (!result.Succeeded)
            {
                errors.Add(result.FailureReason ?? result.StandardError.Trim());
            }

            switch (request.OperationType)
            {
                case OperationTypes.FastbootDevices:
                    IReadOnlyList<DeviceInfoDto> devices = DeviceDetector.ParseFastbootDevices(result.StandardOutput);
                    checks.Add(new DiagnosticCheckDto("fastboot-devices", devices.Count > 0 ? "pass" : "warn",
                        devices.Count > 0 ? $"{devices.Count} device(s) in fastboot mode." : "No fastboot device was attached.", null));
                    break;

                case OperationTypes.FastbootReboot:
                    if (result.Succeeded)
                    {
                        warnings.Add($"Reboot to {request.GetParameter("target") ?? "system"} was requested from fastboot.");
                    }

                    break;
            }

            bool succeeded = result.Succeeded;
            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                succeeded ? "Completed." : errors.FirstOrDefault() ?? "The fastboot command failed.",
                checks, log, warnings, errors);
        }

        /// <summary>Maps the requested reboot target onto the fastboot command that reaches it.</summary>
        private static string[] RebootArguments(OperationExecutionRequest request)
        {
            List<string> args = new();
            if (request.Serial is not null)
            {
                args.AddRange(["-s", request.Serial]);
            }

            string target = (request.GetParameter("target") ?? "system").Trim().ToLowerInvariant();
            switch (target)
            {
                case "edl":
                    args.AddRange(["oem", "edl"]);
                    break;

                case "system":
                    args.Add("reboot");
                    break;

                default:
                    args.AddRange(["reboot", target]);
                    break;
            }

            return args.ToArray();
        }

        /// <summary>
        /// Runs <c>fastboot getvar all</c> and prints a readable report beside the raw output. fastboot
        /// writes those variables to stderr on most builds, so both streams are merged and parsed.
        /// </summary>
        private async Task<OperationExecutionResult> ReadInfoAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "Fastboot read requires a device in bootloader mode.",
                    "No device serial was supplied for this operation.");
            }

            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();
            List<DiagnosticCheckDto> checks = new();

            ProcessResult result = await Run(log, ["-s", request.Serial, "getvar", "all"], 90, ct);
            Dictionary<string, string> values = ParseVariables(result.StandardOutput + "\n" + result.StandardError);

            if (values.Count == 0)
            {
                errors.Add(result.FailureReason ?? "fastboot did not return any variable.");
                return new OperationExecutionResult(request.OperationType, false, OperationOutcome.Failed,
                    errors[0], checks, log, warnings, errors);
            }

            (string Label, string Key)[] wanted =
            [
                ("Product", "product"),
                ("Variant", "variant"),
                ("Serial", "serialno"),
                ("Bootloader", "version-bootloader"),
                ("Baseband", "version-baseband"),
                ("Unlocked", "unlocked"),
                ("Secure", "secure"),
                ("Current slot", "current-slot"),
                ("Slot count", "slot-count"),
                ("Max download size", "max-download-size")
            ];

            log.Add(string.Empty);
            log.Add("=== Fastboot variables ===");
            foreach ((string label, string key) in wanted)
            {
                string value = values.TryGetValue(key, out string? found) ? found : "—";
                log.Add($"{label,-18}: {value}");

                if (!string.Equals(value, "—", StringComparison.Ordinal))
                {
                    checks.Add(new DiagnosticCheckDto(key, "pass", value, null));
                }
            }

            if (values.TryGetValue("secure", out string? secure) && secure.Contains("yes", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("The device reports secure boot enabled; partition erases may be refused by the bootloader.");
            }

            string product = values.GetValueOrDefault("product", "device");
            return new OperationExecutionResult(request.OperationType, true, OperationOutcome.Succeeded,
                $"{product} — {values.Count} fastboot variable(s) read.", checks, log, warnings, errors);
        }

        /// <summary>
        /// Erases the three partitions that carry the Google account lock. A partition the device does
        /// not have is reported as a warning; any other refusal fails the operation.
        /// </summary>
        private async Task<OperationExecutionResult> ResetFrpAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();
            List<DiagnosticCheckDto> checks = new();

            bool frpErased = false;
            foreach (string partition in new[] { "frp", "config", "persistent" })
            {
                ProcessResult result = await Run(log, ["-s", request.Serial!, "erase", partition], 120, ct);

                if (result.Succeeded)
                {
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "pass", $"Partition '{partition}' erased.", result.DurationMs));
                    frpErased |= partition == "frp";
                    continue;
                }

                if (IsMissingPartition(result))
                {
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "warn",
                        $"The device has no '{partition}' partition; skipped.", result.DurationMs));
                    continue;
                }

                checks.Add(new DiagnosticCheckDto($"erase-{partition}", "fail",
                    result.FailureReason ?? result.StandardError.Trim(), result.DurationMs));
                errors.Add($"{partition}: {result.FailureReason ?? result.StandardError.Trim()}");
            }

            warnings.Add("FRP erasure is only accepted by an unlocked bootloader, and the handset must stay connected until it reboots.");

            bool succeeded = frpErased && errors.Count == 0;

            // The sequence ends with a reboot: the setup wizard reads the erase result as it starts,
            // so leaving the handset in the bootloader only half-completes the routine.
            if (succeeded)
            {
                ProcessResult reboot = await Run(log, ["-s", request.Serial!, "reboot"], 90, ct);
                checks.Add(new DiagnosticCheckDto("reboot", reboot.Succeeded ? "pass" : "warn",
                    reboot.Succeeded ? "Reboot to system requested." : "The reboot was refused; power the handset on by hand.",
                    reboot.DurationMs));

                if (!reboot.Succeeded)
                {
                    warnings.Add("The partitions were erased but the handset did not reboot. Unplug it and power it on by hand.");
                }
            }
            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                succeeded
                    ? "FRP partitions erased and the handset was rebooted."
                    : errors.FirstOrDefault() ?? "The 'frp' partition was not erased.",
                checks, log, warnings, errors);
        }

        /// <summary>
        /// Erases the partition list a vendor workflow asks for ("frp,config", "userdata,cache",
        /// "frp,persistent"). The request must be confirmed explicitly, and the calibration and
        /// boot-chain partitions in <see cref="ProtectedPartitions"/> are refused by name.
        /// </summary>
        private async Task<OperationExecutionResult> EraseBatchAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();
            List<DiagnosticCheckDto> checks = new();

            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required.", "No device serial was supplied for this operation.");
            }

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Erasing partitions must be confirmed explicitly before it runs. " +
                           "Confirm the warning in the client and retry.");
                return VendorSupport.Build(request.OperationType, "Confirmation required.", checks, log, warnings, errors);
            }

            string[] partitions = (request.GetParameter("partitions") ?? string.Empty)
                .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (partitions.Length == 0)
            {
                errors.Add("No partition was named. Supply the partition list to erase.");
                return VendorSupport.Build(request.OperationType, "No partition selected.", checks, log, warnings, errors);
            }

            string[] protectedRequested = partitions.Where(p => ProtectedPartitions.Contains(p)).ToArray();
            if (protectedRequested.Length > 0)
            {
                errors.Add($"Refusing to erase protected partition(s): {string.Join(", ", protectedRequested)}. " +
                           "These carry radio calibration or the boot chain and cannot be restored.");
                checks.Add(new DiagnosticCheckDto("protected-partitions", "fail",
                    $"Blocked: {string.Join(", ", protectedRequested)}", null));
                return VendorSupport.Build(request.OperationType, "Protected partition requested.", checks, log, warnings, errors);
            }

            string? fastboot = _tools.Find(ToolLocator.Fastboot);
            if (fastboot is null)
            {
                errors.Add("fastboot was not found. Install the bundled platform-tools next to the application.");
                return VendorSupport.Build(request.OperationType, "fastboot is unavailable.", checks, log, warnings, errors);
            }

            request.Process($"Erasing {string.Join(", ", partitions)} on {request.Serial}.");

            ProcessResult devices = await Run(log, ["devices"], 30, ct);
            if (string.IsNullOrWhiteSpace(devices.StandardOutput))
            {
                errors.Add("No fastboot device answered. Boot the handset into fastboot/bootloader mode and retry.");
                checks.Add(new DiagnosticCheckDto("fastboot-device", "fail", "No fastboot device.", null));
                return VendorSupport.Build(request.OperationType, "No fastboot device.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("fastboot-device", "pass", "A fastboot device is attached.", null));

            bool allSucceeded = true;
            foreach (string partition in partitions)
            {
                ProcessResult erase = await Run(log, ["-s", request.Serial, "erase", partition], 180, ct);
                string output = FirstLine(erase.StandardOutput + " " + erase.StandardError);

                if (erase.Succeeded)
                {
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "pass", $"Partition '{partition}' erased.", erase.DurationMs));
                    request.Success($"Erased '{partition}'.");
                    continue;
                }

                if (IsMissingPartition(erase))
                {
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "warn",
                        $"The device has no '{partition}' partition; skipped.", erase.DurationMs));
                    warnings.Add($"Partition '{partition}' does not exist on this handset and was skipped.");
                    continue;
                }

                allSucceeded = false;
                checks.Add(new DiagnosticCheckDto($"erase-{partition}", "fail",
                    VendorSupport.Truncate(output, 200), erase.DurationMs));
                errors.Add($"fastboot erase {partition} failed: {VendorSupport.Truncate(output, 200)}");
            }

            warnings.Add("Reboot the handset after the erase and confirm that setup completes normally.");

            string summary = allSucceeded
                ? $"Erased {string.Join(", ", partitions)}."
                : "The erase sequence finished with errors.";

            return VendorSupport.Build(request.OperationType, summary, checks, log, warnings, errors);
        }

        private static bool IsMissingPartition(ProcessResult result)
        {
            string text = (result.StandardError + " " + result.StandardOutput).ToLowerInvariant();
            return text.Contains("doesn't exist", StringComparison.Ordinal) ||
                   text.Contains("does not exist", StringComparison.Ordinal) ||
                   text.Contains("no such partition", StringComparison.Ordinal) ||
                   text.Contains("partition not found", StringComparison.Ordinal) ||
                   text.Contains("not found", StringComparison.Ordinal);
        }

        /// <summary>Parses <c>(bootloader) key: value</c> lines from either output stream.</summary>
        internal static Dictionary<string, string> ParseVariables(string output)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string line = raw.StartsWith("(bootloader)", StringComparison.OrdinalIgnoreCase)
                    ? raw["(bootloader)".Length..].Trim()
                    : raw;

                int separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                if (key.Length > 0 && !key.StartsWith("fastboot", StringComparison.OrdinalIgnoreCase))
                {
                    values[key] = value;
                }
            }

            return values;
        }

        private async Task<ProcessResult?> FlashAsync(OperationExecutionRequest request, List<string> log, List<string> warnings, CancellationToken ct)
        {
            if (request.Serial is null)
            {
                return null;
            }

            string? partition = request.GetParameter("partition");
            string? imagePath = request.GetParameter("image");
            if (string.IsNullOrWhiteSpace(partition) || string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            if (!File.Exists(imagePath))
            {
                log.Add($"The image file '{imagePath}' does not exist.");
                return null;
            }

            warnings.Add($"Flashing partition '{partition}' can brick the handset if the image is wrong. The device must stay connected.");
            return await Run(log, ["-s", request.Serial!, "flash", partition, imagePath], 600, ct);
        }

        private async Task<ProcessResult?> WipeAsync(OperationExecutionRequest request, List<string> log, List<string> warnings, CancellationToken ct)
        {
            if (request.Serial is null)
            {
                return null;
            }

            string? partition = request.GetParameter("partition");
            if (string.IsNullOrWhiteSpace(partition))
            {
                return null;
            }

            warnings.Add($"Erasing partition '{partition}' permanently removes its contents. This cannot be undone from the tool.");
            return await Run(log, ["-s", request.Serial!, "erase", partition], 300, ct);
        }

        private static string FirstLine(string value)
            => value.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

        private async Task<ProcessResult> Run(List<string> log, string[] args, int timeout, CancellationToken ct)
        {
            ProcessResult result = await _adb.FastbootAsync(args, timeout, ct);
            log.Add($"$ fastboot {string.Join(' ', args)}");
            log.AddRange(AdbOperationExecutor.AppendOutput(result));
            return result;
        }
    }
}
