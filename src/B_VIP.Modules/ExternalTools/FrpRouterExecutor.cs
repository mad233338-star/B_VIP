using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// One Reset FRP button for the Infinix, Tecno and Xiaomi screens. It reads the chipset the handset
    /// really reports (adb <c>ro.board.platform</c> / <c>ro.hardware</c>, or the operator's explicit
    /// choice) and redirects the routine to the engine that chipset needs:
    ///
    /// <list type="bullet">
    ///   <item>MediaTek → mtkclient through the boot ROM.</item>
    ///   <item>Unisoc/Spreadtrum → spd_dump with the FDL stages.</item>
    ///   <item>Qualcomm in EDL → fh_loader on the 9008 port.</item>
    ///   <item>Anything else, or a handset in bootloader mode → the fastboot erase sequence.</item>
    /// </list>
    ///
    /// A Xiaomi handset with a locked bootloader cannot be erased at all, so it is routed to the
    /// Mi Assistant / sideload procedure instead of failing on "erase not allowed".
    /// </summary>
    public sealed class FrpRouterExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;
        private readonly IProcessRunner _runner;
        private readonly IVendorToolchain _toolchain;

        public FrpRouterExecutor(IAdbClient adb, IProcessRunner runner, IVendorToolchain? toolchain = null)
        {
            _adb = adb;
            _runner = runner;
            _toolchain = toolchain ?? new VendorToolchain();
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.FrpAutoReset];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                return OperationExecutionResult.Failure(request.OperationType, "Confirmation required.",
                    "Erasing FRP must be confirmed explicitly before it runs. Confirm the warning in the client and retry.");
            }

            string brand = (request.GetParameter("brand") ?? "vendor").Trim().ToLowerInvariant();
            string mode = (request.GetParameter("mode") ?? "auto").Trim().ToLowerInvariant();

            if (mode == "auto")
            {
                string? chipset = request.GetParameter("chipset");
                if (string.IsNullOrWhiteSpace(chipset) || chipset.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    chipset = await DetectChipsetAsync(request.Serial, ct);
                }

                mode = RouteFor(chipset, brand);
                request.Info($"FRP route: {Describe(mode, chipset)}");
            }

            OperationExecutionRequest delegated = request with { OperationType = OperationFor(mode, request) };
            OperationExecutionResult result;

            switch (mode)
            {
                case "mtk":
                    result = await new MtkClientFrpExecutor(_runner, _toolchain).ExecuteAsync(
                        delegated with { OperationType = OperationTypes.MtkFrpClient }, ct);
                    break;

                case "spd":
                    result = await new SpdDumpFrpExecutor(_runner, _toolchain).ExecuteAsync(
                        delegated with { OperationType = OperationTypes.SpdFrpDump }, ct);
                    break;

                case "qualcomm":
                    result = await new FhLoaderFrpExecutor(_runner, _toolchain).ExecuteAsync(
                        delegated with { OperationType = OperationTypes.QualcommFrpLoader }, ct);
                    break;

                case "adb":
                    result = await new SetupWizardBypassExecutor(_adb).ExecuteAsync(
                        delegated with { OperationType = OperationTypes.AdbSetupWizardBypass }, ct);
                    break;

                case "assistant":
                    result = await new XiaomiMiAssistantExecutor(_adb).ExecuteAsync(
                        delegated with { OperationType = OperationTypes.XiaomiMiAssistant }, ct);
                    break;

                default:
                    result = await FastbootAsync(request, brand, ct);
                    break;
            }

            // The callers only know the routed operation, so report under that name.
            return result with { OperationType = request.OperationType };
        }

        /// <summary>Maps a requested route (or a detected chipset) onto an engine.</summary>
        internal static string RouteFor(string? chipset, string brand)
        {
            string value = (chipset ?? string.Empty).Trim().ToLowerInvariant();

            if (value.Length == 0)
            {
                return "fastboot";
            }

            if (value is "qualcomm" or "edl" or "9008" or "sahara")
            {
                return "qualcomm";
            }

            if (value is "unisoc" or "spreadtrum" or "sprd" or "spd")
            {
                return "spd";
            }

            // Unisoc names its SoCs ums512, sp9820e, sc9863a: a two-letter family followed by a digit.
            if (value.StartsWith("ums", StringComparison.Ordinal) ||
                value.StartsWith("sprd", StringComparison.Ordinal) ||
                value.Contains("unisoc", StringComparison.Ordinal) ||
                value.Contains("spreadtrum", StringComparison.Ordinal) ||
                IsFamilyWithNumber(value, "sp") ||
                IsFamilyWithNumber(value, "sc"))
            {
                return "spd";
            }

            if (value.StartsWith("mt", StringComparison.Ordinal) ||
                value.Contains("mediatek", StringComparison.Ordinal) ||
                value.Contains("helio", StringComparison.Ordinal) ||
                value.Contains("dimensity", StringComparison.Ordinal))
            {
                return "mtk";
            }

            if (value.StartsWith("sm", StringComparison.Ordinal) ||
                value.StartsWith("msm", StringComparison.Ordinal) ||
                value.StartsWith("sdm", StringComparison.Ordinal) ||
                value.StartsWith("sdx", StringComparison.Ordinal) ||
                value.Contains("qcom", StringComparison.Ordinal) ||
                value.Contains("qualcomm", StringComparison.Ordinal) ||
                value.Contains("taro", StringComparison.Ordinal) ||
                value.Contains("lahaina", StringComparison.Ordinal) ||
                value.Contains("kona", StringComparison.Ordinal))
            {
                // A Qualcomm handset in Android is reachable through fastboot; EDL needs the 9008 port
                // and a programmer, which the operator selects on the Qualcomm screen.
                return "fastboot";
            }

            return "fastboot";
        }

        /// <summary>True for a family code such as "sp9820e" or "sc9863a" (prefix + a model number).</summary>
        private static bool IsFamilyWithNumber(string value, string prefix)
            => value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.Length > prefix.Length &&
               char.IsDigit(value[prefix.Length]);

        internal static string Describe(string mode, string? chipset) => mode switch
        {
            "mtk" => $"MediaTek boot ROM (mtkclient), chipset {chipset ?? "unknown"}",
            "spd" => $"Unisoc boot ROM (spd_dump), chipset {chipset ?? "unknown"}",
            "qualcomm" => $"Qualcomm EDL (fh_loader), chipset {chipset ?? "unknown"}",
            "adb" => "Android setup wizard over ADB",
            "assistant" => "Mi Assistant / sideload",
            _ => $"fastboot erase sequence, chipset {chipset ?? "unknown"}"
        };

        private static string OperationFor(string mode, OperationExecutionRequest request) => mode switch
        {
            "mtk" => OperationTypes.MtkFrpClient,
            "spd" => OperationTypes.SpdFrpDump,
            "qualcomm" => OperationTypes.QualcommFrpLoader,
            "adb" => OperationTypes.AdbSetupWizardBypass,
            "assistant" => OperationTypes.XiaomiMiAssistant,
            _ => request.OperationType
        };

        /// <summary>Reads the platform property chain; null when adb is not available or not authorized.</summary>
        private async Task<string?> DetectChipsetAsync(string? serial, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                return null;
            }

            try
            {
                IReadOnlyList<Dictionary<string, string>> properties = await _adb.GetPropertiesAsync(serial, ct);
                if (properties.Count == 0)
                {
                    return null;
                }

                return VendorSupport.Any(properties,
                    "ro.board.platform", "ro.hardware", "ro.soc.model", "ro.chipname", "ro.mediatek.platform");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// The fastboot route: erase exactly the partitions this brand's FRP routine needs, then
        /// reboot. A protected partition (modem calibration, boot chain) is refused by name.
        /// </summary>
        private async Task<OperationExecutionResult> FastbootAsync(OperationExecutionRequest request, string brand, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            string[] partitions = PartitionList(request.GetParameter("partitions"), brand);

            request.Process($"Erasing {string.Join(", ", partitions)} through fastboot.");

            ProcessResult listing = await _adb.FastbootAsync(["devices"], 30, ct);
            log.Add("$ fastboot devices");

            bool attached = !string.IsNullOrWhiteSpace(listing.StandardOutput);
            checks.Add(new DiagnosticCheckDto("fastboot-device", attached ? "pass" : "fail",
                attached
                    ? VendorSupport.Truncate(listing.StandardOutput.Replace(Environment.NewLine, " ").Trim(), 180)
                    : "No fastboot device answered. Boot the handset into bootloader mode and retry.", listing.DurationMs));

            if (!attached)
            {
                errors.Add("The handset is not in fastboot mode. Power it off and hold Vol- (or Vol- + Power) to reach the bootloader.");

                if (brand == "xiaomi")
                {
                    errors.Add("If the bootloader is locked, use 'Mi Assistant / sideload' instead: fastboot cannot erase a locked Xiaomi.");
                }

                return VendorSupport.Build(request.OperationType, "No fastboot device.", checks, log, warnings, errors);
            }

            // A locked Xiaomi bootloader refuses every erase, so route it to Mi Assistant before trying.
            if (brand == "xiaomi")
            {
                ProcessResult unlocked = await _adb.FastbootAsync(["getvar", "unlocked"], 30, ct);
                string value = FirstLine(unlocked.StandardOutput + " " + unlocked.StandardError);
                log.Add($"$ fastboot getvar unlocked -> {VendorSupport.Truncate(value, 120)}");

                bool locked = value.Contains("no", StringComparison.OrdinalIgnoreCase) &&
                              !value.Contains("not", StringComparison.OrdinalIgnoreCase);

                checks.Add(new DiagnosticCheckDto("bootloader", locked ? "warn" : "pass",
                    locked ? "The bootloader reports 'unlocked: no'." : VendorSupport.Truncate(value, 160), unlocked.DurationMs));

                if (locked)
                {
                    warnings.Add("The bootloader is locked, so fastboot will refuse the erase. Use 'Mi Assistant / sideload' " +
                                 "with a signed firmware for this model, then sign the account in once.");
                    errors.Add("A locked Xiaomi bootloader cannot erase 'frp'. Route the handset through Mi Assistant / sideload.");
                    return VendorSupport.Build(request.OperationType, "Bootloader is locked.", checks, log, warnings, errors);
                }
            }

            string? serial = request.Serial;
            bool anyFailed = false;

            foreach (string partition in partitions)
            {
                List<string> args = serial is null ? ["erase", partition] : ["-s", serial, "erase", partition];
                ProcessResult erase = await _adb.FastbootAsync(args, 240, ct);
                string output = FirstLine(erase.StandardOutput + " " + erase.StandardError);

                log.Add($"$ fastboot {string.Join(' ', args)}");
                if (output.Length > 0)
                {
                    log.Add("  " + VendorSupport.Truncate(output, 220));
                }

                bool missing = (erase.StandardError + " " + erase.StandardOutput)
                    .Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
                    || (erase.StandardError + " " + erase.StandardOutput)
                    .Contains("not found", StringComparison.OrdinalIgnoreCase);

                if (erase.Succeeded)
                {
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "pass", $"'{partition}' erased.", erase.DurationMs));
                    request.Success($"Erased '{partition}'.");
                }
                else if (missing)
                {
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "warn",
                        $"This handset has no '{partition}' partition; skipped.", erase.DurationMs));
                    warnings.Add($"Partition '{partition}' does not exist on this handset and was skipped.");
                }
                else
                {
                    anyFailed = true;
                    string reason = string.IsNullOrWhiteSpace(erase.FailureReason)
                        ? VendorSupport.Truncate(output.Length == 0 ? "refused by the bootloader" : output, 220)
                        : erase.FailureReason!;
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "fail", reason, erase.DurationMs));
                    errors.Add($"fastboot erase {partition} failed: {reason}");
                }
            }

            if (!anyFailed)
            {
                request.Process("Rebooting the handset.");
                List<string> rebootArgs = serial is null ? ["reboot"] : ["-s", serial, "reboot"];
                ProcessResult reboot = await _adb.FastbootAsync(rebootArgs, 60, ct);
                log.Add($"$ fastboot {string.Join(' ', rebootArgs)}");

                checks.Add(new DiagnosticCheckDto("reboot", reboot.Succeeded ? "pass" : "warn",
                    reboot.Succeeded ? "Reboot requested." : "The reboot was refused; power the handset on by hand.",
                    reboot.DurationMs));
            }

            warnings.Add("Complete the setup once and confirm the Google account step no longer appears.");

            return VendorSupport.Build(request.OperationType,
                anyFailed
                    ? "The fastboot erase sequence finished with errors."
                    : $"Erased {string.Join(", ", partitions)} and rebooted the handset.",
                checks, log, warnings, errors);
        }

        internal static string[] PartitionList(string? requested, string brand)
        {
            string fallback = brand switch
            {
                "xiaomi" => "frp,config",
                "tecno" => "frp,frp_p",
                _ => "frp,persist"
            };

            string[] asked = (requested ?? fallback)
                .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.ToLowerInvariant())
                .ToArray();

            return asked.Length == 0 ? fallback.Split(',') : asked;
        }

        private static string FirstLine(string value) => VendorSupport.FirstLine(value);
    }
}
