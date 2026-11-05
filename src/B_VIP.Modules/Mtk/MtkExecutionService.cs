using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.Mtk
{
    /// <summary>
    /// MediaTek (MTK) engine.
    ///
    /// What this module can do on its own: find the BROM/preloader/VCOM interfaces, read everything
    /// the handset exposes over adb, and drive the partition routines that fastboot accepts on an
    /// unlocked bootloader (<c>erase frp</c>, <c>fastboot -w</c>, scatter-guided flashing).
    ///
    /// What it cannot do: talk to the boot ROM. That protocol is binary and driven by the vendor
    /// download agent plus a libusb transport; nothing in managed code speaks it. The handshake step
    /// therefore probes the port and reports whether a download agent is present, and the operations
    /// that need one say so instead of pretending to erase anything.
    /// </summary>
    public sealed class MtkExecutionService
    {
        /// <summary>Partitions a factory format is allowed to touch.</summary>
        private static readonly string[] FormatPartitions = ["userdata", "cache", "metadata"];

        /// <summary>Names of the vendor download agents the engine can drive if the operator supplies one.</summary>
        private static readonly string[] DownloadAgentPatterns =
        [
            "MTK_AllInOne_DA*.bin",
            "mtk_allinone_da*.bin",
            "DA_*.bin",
            "mtkclient*"
        ];

        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;

        public MtkExecutionService(IAdbClient adb, IToolLocator tools)
        {
            _adb = adb;
            _tools = tools;
        }

        /// <summary>
        /// Reads the platform identity over adb and lists every MediaTek interface Windows currently
        /// reports, so the operator can see whether the handset is in Android, preloader or BROM.
        /// </summary>
        public async Task<OperationExecutionResult> ReadInfoAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Info("Step 1/2 — enumerating MediaTek USB interfaces.");
            IReadOnlyList<MtkPort> ports = ListPorts();
            log.Add(ports.Count == 0
                ? "MTK interfaces: none"
                : $"MTK interfaces: {string.Join(" | ", ports.Select(p => p.Summary))}");

            checks.Add(new DiagnosticCheckDto("mtk-interfaces", ports.Count > 0 ? "pass" : "skip",
                ports.Count > 0 ? string.Join("; ", ports.Select(p => p.Summary)) : "No MediaTek interface is attached.", null));

            foreach (MtkPort port in ports)
            {
                checks.Add(new DiagnosticCheckDto($"mtk-port-{port.PortName}", "pass",
                    $"{port.KindLabel} on {port.PortName} ({port.UsbId})", null));
            }

            request.Info("Step 2/2 — reading platform properties through adb.");
            string? serial = await ResolveAdbSerialAsync(request.Serial, ct);
            if (serial is null)
            {
                warnings.Add("No authorized adb connection: only the USB interfaces above could be read. Enable USB debugging to read the platform, SoC and build identity.");
                checks.Add(new DiagnosticCheckDto("adb-connection", "skip", "No authorized adb device.", null));
            }
            else
            {
                IReadOnlyList<Dictionary<string, string>> props = await _adb.GetPropertiesAsync(serial, ct);
                Dictionary<string, string> values = Flatten(props);

                if (values.Count == 0)
                {
                    errors.Add("The device answered adb but returned no property; the connection may have dropped.");
                }

                (string Label, string Key)[] wanted =
                [
                    ("Manufacturer", "ro.product.manufacturer"),
                    ("Model", "ro.product.model"),
                    ("Board platform", "ro.board.platform"),
                    ("SoC", "ro.soc.model"),
                    ("Chip", "ro.hardware"),
                    ("Android version", "ro.build.version.release"),
                    ("Build", "ro.build.display.id"),
                    ("Security patch", "ro.build.version.security_patch"),
                    ("Bootloader", "ro.boot.verifiedbootstate"),
                    ("Slot", "ro.boot.slot_suffix")
                ];

                log.Add("=== MediaTek device information ===");
                foreach ((string label, string key) in wanted)
                {
                    string? value = values.GetValueOrDefault(key);
                    log.Add($"{label,-18}: {(string.IsNullOrWhiteSpace(value) ? "—" : value)}");
                    checks.Add(new DiagnosticCheckDto(key, string.IsNullOrWhiteSpace(value) ? "skip" : "pass",
                        string.IsNullOrWhiteSpace(value) ? $"{label} was not reported." : $"{label}: {value}", null));
                }

                string? platform = values.GetValueOrDefault("ro.board.platform")
                    ?? values.GetValueOrDefault("ro.hardware")
                    ?? values.GetValueOrDefault("ro.mediatek.platform");

                bool isMediaTek = platform is not null &&
                    (platform.StartsWith("mt", StringComparison.OrdinalIgnoreCase) ||
                     platform.Contains("mediatek", StringComparison.OrdinalIgnoreCase));
                checks.Add(new DiagnosticCheckDto("mediatek-platform", isMediaTek ? "pass" : "warn",
                    isMediaTek ? $"Board platform: {platform}" : $"Platform '{platform ?? "unknown"}' does not look MediaTek.", null));

                if (!isMediaTek)
                {
                    warnings.Add("The connected handset does not report a MediaTek platform; the MTK routines will refuse it.");
                }
            }

            bool succeeded = errors.Count == 0 && checks.Any(c => c.Status == "pass");
            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? (warnings.Count > 0 ? OperationOutcome.RequiresAttention : OperationOutcome.Succeeded) : OperationOutcome.Failed,
                succeeded ? "MediaTek information read." : errors.FirstOrDefault() ?? "Nothing could be read.",
                checks, log, warnings, errors);
        }

        /// <summary>
        /// BROM/preloader handshake. Opens the port, reads whatever the interface emits, and reports
        /// whether a download agent is available to go further. The byte-level BROM protocol is not
        /// spoken here, so a successful probe is reported as such and not as an authenticated link.
        /// </summary>
        public async Task<OperationExecutionResult> HandshakeAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Process("Scanning for MediaTek BROM / preloader interfaces.");
            IReadOnlyList<MtkPort> ports = ListPorts();
            if (ports.Count == 0)
            {
                errors.Add("No MediaTek interface is attached. Hold the handset with the battery disconnected, connect USB, then attach the battery so the SoC enters the boot ROM.");
                checks.Add(new DiagnosticCheckDto("mtk-interfaces", "fail", "No MediaTek interface found.", null));
                return Build(request, "Handshake could not start.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("mtk-interfaces", "pass",
                string.Join("; ", ports.Select(p => p.Summary)), null));

            // 1. Probe each interface for a banner. A preloader usually prints something; the boot ROM
            //    stays silent until a download agent speaks to it.
            bool anyOpened = false;
            foreach (MtkPort port in ports.Take(3))
            {
                request.Process($"Opening {port.PortName} at {MtkSerialProbe.BaudRate} baud.");
                MtkProbeResult probe = await Task.Run(() => MtkSerialProbe.Probe(port.PortName), ct);
                anyOpened |= probe.Opened;

                if (!probe.Opened)
                {
                    checks.Add(new DiagnosticCheckDto($"probe-{port.PortName}", "fail",
                        $"Could not open {port.PortName}: {probe.FailureReason}", null));
                    log.Add($"{port.PortName}: {probe.FailureReason}");
                    continue;
                }

                checks.Add(new DiagnosticCheckDto($"probe-{port.PortName}", probe.ReceivedData ? "pass" : "warn",
                    probe.ReceivedData
                        ? $"{port.PortName} answered: {Truncate(probe.Banner, 240)}"
                        : $"{port.PortName} opened but sent no banner (expected for the boot ROM).",
                    null));

                if (probe.ReceivedData)
                {
                    log.Add($"{port.PortName} banner: {Truncate(probe.Banner, 400)}");
                    request.Success($"{port.KindLabel} answered on {port.PortName}.");
                }
            }

            // 2. Download agent availability: without it, no BROM operation can run.
            request.Process("Looking for a MediaTek download agent.");
            string? agent = FindDownloadAgent();
            checks.Add(new DiagnosticCheckDto("download-agent", agent is null ? "fail" : "pass",
                agent ?? "No download agent (DA) binary was found next to the application.", null));

            if (agent is null)
            {
                warnings.Add("The boot ROM needs a vendor download agent plus a libusb transport; neither is part of this build, so BROM read/write is unavailable. The fastboot-based routines on the MediaTek screen remain available.");
            }

            warnings.Add("SLA / DAA authentication is a boot-ROM handshake performed by that vendor agent; it is not bypassed from managed code.");

            bool succeeded = anyOpened && agent is not null;
            return Build(request,
                agent is null
                    ? "Interfaces detected; the boot-ROM stage is unavailable without a download agent."
                    : "MediaTek interfaces detected and a download agent is available.",
                checks, log, warnings, errors, succeeded);
        }

        /// <summary>
        /// One-click FRP reset. Uses the fastboot path, which is the only path available without a
        /// download agent: an authorized adb device is rebooted into the bootloader first.
        /// </summary>
        public async Task<OperationExecutionResult> ResetFrpAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                return OperationExecutionResult.Skipped(request.OperationType,
                    "The FRP reset was not confirmed.",
                    "The operator did not confirm the FRP reset, so nothing was erased.");
            }

            if (!await EnsureFastbootAsync(request, checks, log, warnings, errors, ct))
            {
                return Build(request, "FRP reset stopped: no device reached fastboot mode.", checks, log, warnings, errors);
            }

            string? serial = request.Serial ?? await ResolveFastbootSerialAsync(ct);
            if (serial is null)
            {
                errors.Add("No fastboot device reports a serial, so the erase command cannot be addressed.");
                return Build(request, "FRP reset stopped.", checks, log, warnings, errors);
            }

            request.Process("Erasing the FRP partition.");
            bool frpErased = false;

            foreach (string partition in new[] { "frp", "persistent" })
            {
                ProcessResult result = await RunFastbootAsync(["-s", serial, "erase", partition], 180, log, ct);

                if (result.Succeeded)
                {
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "pass", $"'{partition}' erased.", result.DurationMs));
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

            // Verifying the partition exists and is now blank is the closest thing fastboot offers to
            // a read-back; the bootloader reports its size, not its contents.
            ProcessResult size = await RunFastbootAsync(["-s", serial, "getvar", "partition-size:frp"], 30, log, ct);
            if (size.Started)
            {
                checks.Add(new DiagnosticCheckDto("verify-frp", size.Succeeded ? "pass" : "warn",
                    $"partition-size:frp reported.", size.DurationMs));
            }

            if (frpErased && errors.Count == 0)
            {
                request.Success("FRP partition erased.");
                warnings.Add("Disconnect the handset, boot it normally and complete the setup without signing in to a Google account.");
            }

            bool succeeded = frpErased && errors.Count == 0;
            return Build(request,
                succeeded ? "FRP erased through fastboot." : errors.FirstOrDefault() ?? "The 'frp' partition was not erased.",
                checks, log, warnings, errors, succeeded);
        }

        /// <summary>
        /// Factory reset: wipes user data and cache and leaves every calibration partition alone. The
        /// protected set is refused explicitly even if a parameter names it.
        /// </summary>
        public async Task<OperationExecutionResult> FactoryResetAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                return OperationExecutionResult.Skipped(request.OperationType,
                    "The factory reset was not confirmed.",
                    "The operator did not confirm the factory reset, so nothing was erased.");
            }

            string? requested = request.GetParameter("partition");
            if (!string.IsNullOrWhiteSpace(requested) && MtkScatterTable.IsProtected(requested))
            {
                errors.Add($"'{requested}' carries calibration or boot data and is never erased by this module.");
                checks.Add(new DiagnosticCheckDto("protected-partition", "fail", errors[0], null));
                return Build(request, "Factory reset refused.", checks, log, warnings, errors);
            }

            if (!await EnsureFastbootAsync(request, checks, log, warnings, errors, ct))
            {
                return Build(request, "Factory reset stopped: no device reached fastboot mode.", checks, log, warnings, errors);
            }

            string? serial = request.Serial ?? await ResolveFastbootSerialAsync(ct);
            if (serial is null)
            {
                errors.Add("No fastboot device reports a serial, so the erase commands cannot be addressed.");
                return Build(request, "Factory reset stopped.", checks, log, warnings, errors);
            }

            request.Process("Wiping userdata and cache.");
            ProcessResult format = await RunFastbootAsync(["-s", serial, "-w"], 900, log, ct);
            checks.Add(new DiagnosticCheckDto("fastboot-w", format.Succeeded ? "pass" : "warn",
                format.Succeeded ? "fastboot -w completed." : format.FailureReason ?? format.StandardError.Trim(),
                format.DurationMs));

            int wiped = 0;
            foreach (string partition in FormatPartitions)
            {
                ProcessResult result = await RunFastbootAsync(["-s", serial, "erase", partition], 300, log, ct);
                if (result.Succeeded)
                {
                    wiped++;
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "pass", $"'{partition}' erased.", result.DurationMs));
                    continue;
                }

                checks.Add(new DiagnosticCheckDto($"erase-{partition}", IsMissingPartition(result) ? "warn" : "fail",
                    IsMissingPartition(result) ? $"The device has no '{partition}' partition." : result.FailureReason ?? result.StandardError.Trim(),
                    result.DurationMs));

                if (!IsMissingPartition(result))
                {
                    errors.Add($"{partition}: {result.FailureReason ?? result.StandardError.Trim()}");
                }
            }

            checks.Add(new DiagnosticCheckDto("calibration-safeguard", "pass",
                "nvram, nvdata, nvcfg, protect1/2 and modem were not touched.", null));

            warnings.Add("A factory reset removes the account binding on the handset, not the Google lock: use the FRP routine first on a protected device.");

            bool succeeded = errors.Count == 0 && (format.Succeeded || wiped > 0);
            return Build(request,
                succeeded ? "Factory format completed." : errors.FirstOrDefault() ?? "The factory format did not complete.",
                checks, log, warnings, errors, succeeded);
        }

        /// <summary>
        /// Parses a scatter table and reports the partition boundaries it declares. With a partition
        /// name and an image path the same routine flashes that image through fastboot.
        /// </summary>
        public async Task<OperationExecutionResult> ScatterAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            string? path = request.GetParameter("scatterPath") ?? request.GetParameter("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add("Supply the path of a scatter file (MTK scatter XML or TXT) to parse.");
                return Build(request, "No scatter file given.", checks, log, warnings, errors);
            }

            if (!File.Exists(path))
            {
                errors.Add($"The scatter file '{path}' does not exist.");
                return Build(request, "Scatter file not found.", checks, log, warnings, errors);
            }

            request.Info($"Parsing {Path.GetFileName(path)}.");
            IReadOnlyList<ScatterPartition> partitions;
            try
            {
                partitions = MtkScatterTable.Parse(await File.ReadAllTextAsync(path, ct));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                errors.Add(ex.Message);
                return Build(request, "The scatter file could not be read.", checks, log, warnings, errors);
            }

            log.Add($"=== Scatter table: {Path.GetFileName(path)} ({partitions.Count} partitions) ===");
            foreach (ScatterPartition partition in partitions)
            {
                log.Add(partition.Summary);
            }

            checks.Add(new DiagnosticCheckDto("scatter-parse", "pass", $"{partitions.Count} partition(s) parsed.", null));

            ScatterPartition? frp = MtkScatterTable.FindFrp(partitions);
            checks.Add(new DiagnosticCheckDto("scatter-frp", frp is null ? "warn" : "pass",
                frp is null
                    ? "The table declares no 'frp' partition."
                    : $"frp: 0x{frp.StartAddress:X8} length 0x{frp.SizeBytes:X8}", null));

            int calibration = partitions.Count(p => p.IsCalibration);
            checks.Add(new DiagnosticCheckDto("scatter-protected", "pass",
                $"{calibration} partition(s) are treated as calibration data and never erased.", null));

            // Optional targeted flash of one image from the table.
            string? target = request.GetParameter("partition");
            string? image = request.GetParameter("image");
            if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(image))
            {
                bool known = partitions.Any(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (!known)
                {
                    errors.Add($"'{target}' is not declared by this scatter table.");
                }
                else if (!File.Exists(image))
                {
                    errors.Add($"The image '{image}' does not exist.");
                }
                else if (!await EnsureFastbootAsync(request, checks, log, warnings, errors, ct))
                {
                    errors.Add("No fastboot device is attached, so the image cannot be written.");
                }
                else
                {
                    string? serial = request.Serial ?? await ResolveFastbootSerialAsync(ct);
                    if (serial is null)
                    {
                        errors.Add("No fastboot device reports a serial.");
                    }
                    else
                    {
                        warnings.Add($"Flashing '{target}' is destructive if the image does not match this exact firmware.");
                        request.Process($"Flashing {target}.");
                        ProcessResult flash = await RunFastbootAsync(["-s", serial, "flash", target, image], 900, log, ct);
                        checks.Add(new DiagnosticCheckDto($"flash-{target}", flash.Succeeded ? "pass" : "fail",
                            flash.Succeeded ? $"'{target}' written." : flash.FailureReason ?? flash.StandardError.Trim(),
                            flash.DurationMs));

                        if (!flash.Succeeded)
                        {
                            errors.Add(flash.FailureReason ?? flash.StandardError.Trim());
                        }
                    }
                }
            }

            bool succeeded = errors.Count == 0;
            return Build(request,
                succeeded ? $"Scatter table read: {partitions.Count} partition(s)." : errors[0],
                checks, log, warnings, errors, succeeded);
        }

        /// <summary>Reboots the handset from Android or from fastboot to the requested mode.</summary>
        public async Task<OperationExecutionResult> RebootAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            string target = (request.GetParameter("target") ?? "system").Trim().ToLowerInvariant();
            if (target is not ("system" or "fastboot" or "recovery" or "bootloader"))
            {
                errors.Add($"'{target}' is not a supported reboot target.");
                return Build(request, "Unsupported reboot target.", checks, log, warnings, errors);
            }

            string? serial = await ResolveAdbSerialAsync(request.Serial, ct);
            if (serial is not null)
            {
                List<string> args = ["-s", serial, "reboot"];
                if (target != "system")
                {
                    args.Add(target == "bootloader" ? "bootloader" : target);
                }

                request.Process($"Rebooting over adb to {target}.");
                ProcessResult result = await _adb.AdbAsync(args, 60, ct);
                log.Add($"$ adb {string.Join(' ', args)}");
                log.AddRange(AdbOperationExecutor.AppendOutput(result));

                checks.Add(new DiagnosticCheckDto("adb-reboot", result.Succeeded ? "pass" : "fail",
                    result.Succeeded ? $"Reboot to {target} accepted." : result.FailureReason ?? result.StandardError.Trim(),
                    result.DurationMs));

                if (!result.Succeeded)
                {
                    errors.Add(result.FailureReason ?? result.StandardError.Trim());
                }

                warnings.Add("The handset leaves the device list while it reboots; wait for it to reappear in the target mode.");
                return Build(request, result.Succeeded ? $"Reboot to {target} sent." : "The reboot was refused.",
                    checks, log, warnings, errors, result.Succeeded);
            }

            string? fastbootSerial = await ResolveFastbootSerialAsync(ct);
            if (fastbootSerial is null)
            {
                errors.Add("No adb or fastboot device is connected, so no reboot command can be sent.");
                checks.Add(new DiagnosticCheckDto("connection", "fail", errors[0], null));
                return Build(request, "No device to reboot.", checks, log, warnings, errors);
            }

            string[] fastbootArgs = target switch
            {
                "system" => ["-s", fastbootSerial, "reboot"],
                "recovery" => ["-s", fastbootSerial, "oem", "reboot-recovery"],
                "fastboot" or "bootloader" => ["-s", fastbootSerial, "reboot-bootloader"],
                _ => ["-s", fastbootSerial, "reboot"]
            };

            request.Process($"Rebooting from fastboot to {target}.");
            ProcessResult fastbootResult = await RunFastbootAsync(fastbootArgs, 60, log, ct);
            checks.Add(new DiagnosticCheckDto("fastboot-reboot", fastbootResult.Succeeded ? "pass" : "fail",
                fastbootResult.Succeeded ? $"Reboot to {target} accepted." : fastbootResult.FailureReason ?? fastbootResult.StandardError.Trim(),
                fastbootResult.DurationMs));

            if (!fastbootResult.Succeeded)
            {
                errors.Add(fastbootResult.FailureReason ?? fastbootResult.StandardError.Trim());
            }

            return Build(request, fastbootResult.Succeeded ? $"Reboot to {target} sent." : "The reboot was refused.",
                checks, log, warnings, errors, fastbootResult.Succeeded);
        }

        // ---------------------------------------------------------------- helpers

        internal static IReadOnlyList<MtkPort> ListPorts()
            => OperatingSystem.IsWindows() ? MtkPortLocator.FindPorts() : Array.Empty<MtkPort>();

        /// <summary>Finds a MediaTek download agent bundled with the tools, if the operator supplied one.</summary>
        internal string? FindDownloadAgent()
        {
            List<string> roots = new();

            string? adb = _tools.Find(ToolLocator.Adb);
            if (adb is not null)
            {
                roots.Add(Path.GetDirectoryName(adb)!);
            }

            roots.Add(AppContext.BaseDirectory);
            foreach (string sub in new[] { ToolLocator.PlatformToolsFolder, ".tools", "tools", "mtk" })
            {
                roots.Add(Path.Combine(AppContext.BaseDirectory, sub));
            }

            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string pattern in DownloadAgentPatterns)
                {
                    try
                    {
                        string? match = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (match is not null)
                        {
                            return match;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // An unreadable folder must not stop the search.
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Makes sure a fastboot device is available. An authorized adb device is rebooted into the
        /// bootloader and the engine waits for it to appear.
        /// </summary>
        private async Task<bool> EnsureFastbootAsync(OperationExecutionRequest request, List<DiagnosticCheckDto> checks,
            List<string> log, List<string> warnings, List<string> errors, CancellationToken ct)
        {
            string? fastboot = _tools.Find(ToolLocator.Fastboot);
            if (fastboot is null)
            {
                errors.Add("fastboot.exe was not found next to the application or in platform-tools.");
                checks.Add(new DiagnosticCheckDto("fastboot-tool", "fail", errors[0], null));
                return false;
            }

            checks.Add(new DiagnosticCheckDto("fastboot-tool", "pass", fastboot, null));

            if (await ResolveFastbootSerialAsync(ct) is not null)
            {
                checks.Add(new DiagnosticCheckDto("fastboot-device", "pass", "Device already in the bootloader.", null));
                return true;
            }

            string? serial = await ResolveAdbSerialAsync(request.Serial, ct);
            if (serial is null)
            {
                errors.Add("The handset is neither in fastboot mode nor authorized over adb. Reboot it into the bootloader (power + volume down) and retry.");
                checks.Add(new DiagnosticCheckDto("fastboot-device", "fail", errors[0], null));
                return false;
            }

            request.Process($"Rebooting {serial} into the bootloader.");
            ProcessResult reboot = await _adb.AdbSerialAsync(serial, ["reboot", "bootloader"], 60, ct);
            log.Add($"$ adb -s {serial} reboot bootloader");
            log.AddRange(AdbOperationExecutor.AppendOutput(reboot));

            if (!reboot.Succeeded)
            {
                errors.Add($"The device refused the bootloader reboot: {reboot.FailureReason ?? reboot.StandardError.Trim()}");
                checks.Add(new DiagnosticCheckDto("fastboot-device", "fail", errors[0], null));
                return false;
            }

            for (int attempt = 0; attempt < 15 && !ct.IsCancellationRequested; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (await ResolveFastbootSerialAsync(ct) is not null)
                {
                    checks.Add(new DiagnosticCheckDto("fastboot-device", "pass", "Device reached the bootloader.", null));
                    return true;
                }
            }

            errors.Add("The device did not appear in fastboot mode within 30 seconds. Check the USB driver and the cable.");
            checks.Add(new DiagnosticCheckDto("fastboot-device", "fail", errors[0], null));
            warnings.Add("Some OEM bootloaders refuse 'adb reboot bootloader' while the screen is locked.");
            return false;
        }

        private async Task<string?> ResolveAdbSerialAsync(string? preferred, CancellationToken ct)
        {
            ProcessResult result = await _adb.AdbAsync(["devices"], 20, ct);
            if (!result.Succeeded)
            {
                return preferred;
            }

            IReadOnlyList<DeviceInfoDto> devices = DeviceDetector.ParseAdbDevices(result.StandardOutput);
            if (preferred is not null &&
                devices.Any(d => string.Equals(d.Serial, preferred, StringComparison.OrdinalIgnoreCase)))
            {
                return preferred;
            }

            return devices.FirstOrDefault(d =>
                d.State is DeviceConnectionState.Connected or DeviceConnectionState.Recovery)?.Serial;
        }

        private async Task<string?> ResolveFastbootSerialAsync(CancellationToken ct)
        {
            ProcessResult result = await _adb.FastbootAsync(["devices"], 20, ct);
            if (!result.Succeeded)
            {
                return null;
            }

            return DeviceDetector.ParseFastbootDevices(result.StandardOutput).FirstOrDefault()?.Serial;
        }

        private async Task<ProcessResult> RunFastbootAsync(string[] args, int timeout, List<string> log, CancellationToken ct)
        {
            ProcessResult result = await _adb.FastbootAsync(args, timeout, ct);
            log.Add($"$ fastboot {string.Join(' ', args)}");
            log.AddRange(AdbOperationExecutor.AppendOutput(result));
            return result;
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

        private static Dictionary<string, string> Flatten(IReadOnlyList<Dictionary<string, string>> props)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, string> entry in props)
            {
                foreach ((string key, string value) in entry)
                {
                    values[key] = value;
                }
            }

            return values;
        }

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value[..max] + "…";

        private static OperationExecutionResult Build(OperationExecutionRequest request, string summary,
            List<DiagnosticCheckDto> checks, List<string> log, List<string> warnings, List<string> errors,
            bool? succeededOverride = null)
        {
            bool succeeded = succeededOverride ?? errors.Count == 0;

            if (succeededOverride is false && errors.Count == 0)
            {
                errors.Add(summary);
            }

            OperationOutcome outcome = succeeded
                ? (warnings.Count > 0 ? OperationOutcome.RequiresAttention : OperationOutcome.Succeeded)
                : OperationOutcome.Failed;

            return new OperationExecutionResult(request.OperationType, succeeded, outcome,
                succeeded ? summary : errors.FirstOrDefault() ?? summary,
                checks, log, warnings, errors);
        }
    }

    /// <summary>Routes the MediaTek operation types to the MTK engine and the platform diagnostic.</summary>
    public sealed class MtkModuleExecutor : IOperationExecutor
    {
        private readonly MtkExecutionService _mtk;
        private readonly MediaTekModuleExecutor _diagnostics;

        public MtkModuleExecutor(IAdbClient adb, IToolLocator tools)
        {
            _mtk = new MtkExecutionService(adb, tools);
            _diagnostics = new MediaTekModuleExecutor(adb);
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.MediaTekDiagnostic,
            OperationTypes.MtkReadInfo,
            OperationTypes.MtkHandshake,
            OperationTypes.MtkResetFrp,
            OperationTypes.MtkFactoryReset,
            OperationTypes.MtkScatter,
            OperationTypes.MtkReboot
        ];

        public Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
            => request.OperationType switch
            {
                OperationTypes.MtkReadInfo => _mtk.ReadInfoAsync(request, ct),
                OperationTypes.MtkHandshake => _mtk.HandshakeAsync(request, ct),
                OperationTypes.MtkResetFrp => _mtk.ResetFrpAsync(request, ct),
                OperationTypes.MtkFactoryReset => _mtk.FactoryResetAsync(request, ct),
                OperationTypes.MtkScatter => _mtk.ScatterAsync(request, ct),
                OperationTypes.MtkReboot => _mtk.RebootAsync(request, ct),
                _ => _diagnostics.ExecuteAsync(request, ct)
            };
    }
}
