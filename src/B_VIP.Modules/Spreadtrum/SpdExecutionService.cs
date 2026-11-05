using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.Spreadtrum
{
    /// <summary>
    /// Unisoc (Spreadtrum) engine.
    ///
    /// It really does: enumerate the SPRD USB/COM interfaces Windows reports, synchronise with the
    /// boot ROM over the framed SPRD protocol, stream FDL1/FDL2 loader images the operator supplies
    /// (with CRC-checked blocks), read the platform identity over adb, and run the documented
    /// fastboot erase routines for FRP and factory format while refusing every calibration
    /// partition (persist, prodnv, nvram, nvdata, modem, ...) by name.
    ///
    /// It does not pretend to: the boot ROM only exposes storage after FDL1 has been uploaded, so
    /// partition work without a loader runs through fastboot instead of the boot ROM.
    /// </summary>
    public sealed class SpdExecutionService
    {
        /// <summary>Partitions no routine in this module may ever erase.</summary>
        public static readonly string[] ProtectedPartitions =
        [
            "persist",
            "prodnv",
            "nvram",
            "nvdata",
            "nvcfg",
            "prodinfo",
            "modem",
            "modemst1",
            "modemst2",
            "fsg",
            "fsc",
            "protect1",
            "protect2",
            "seccfg",
            "trustos",
            "sml",
            "vbmeta"
        ];

        /// <summary>Partitions the FRP routine erases, in order, when the bootloader reports them.</summary>
        public static readonly string[] FrpPartitions = ["frp", "persistent"];

        /// <summary>Partitions the factory format erases, in order.</summary>
        public static readonly string[] FormatPartitions = ["userdata", "cache"];

        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;
        private readonly IProcessRunner _runner;
        private readonly ISpdTransportFactory _transports;
        private readonly Func<IReadOnlyList<SpdPort>> _portEnumerator;

        public SpdExecutionService(IAdbClient adb, IToolLocator tools, IProcessRunner runner,
            ISpdTransportFactory? transports = null,
            Func<IReadOnlyList<SpdPort>>? portEnumerator = null)
        {
            _adb = adb;
            _tools = tools;
            _runner = runner;
            _transports = transports ?? new SpdSerialTransportFactory();
            _portEnumerator = portEnumerator ?? DefaultPorts;
        }

        /// <summary>True when the partition name carries calibration or boot data.</summary>
        public static bool IsProtected(string? partitionName)
            => !string.IsNullOrWhiteSpace(partitionName) &&
               ProtectedPartitions.Contains(partitionName.Trim(), StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- read info

        /// <summary>Reports the attached SPRD interfaces and, over adb, the handset's identity.</summary>
        public async Task<OperationExecutionResult> ReadInfoAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Info("Step 1/2 — enumerating Unisoc USB/COM interfaces.");
            IReadOnlyList<SpdPort> ports = ListPorts();

            foreach (SpdPort port in ports)
            {
                if (port.Kind is SpdPortKind.Brom or SpdPortKind.Fdl)
                {
                    request.Info($"SPRD loader port detected on {port.PortName} ({port.UsbId}).");
                }
            }

            checks.Add(new DiagnosticCheckDto("spd-interfaces", ports.Count > 0 ? "pass" : "skip",
                ports.Count > 0 ? string.Join("; ", ports.Select(p => p.Summary)) : "No Unisoc interface is attached.",
                null));

            request.Info("Step 2/2 — reading platform properties through adb.");
            string? serial = await ResolveSerialAsync(request.Serial, ct);
            if (serial is null)
            {
                warnings.Add("No authorized adb connection: boot the handset normally with USB debugging, " +
                             "or use the SPD handshake/FDL routines for a dead device.");
                checks.Add(new DiagnosticCheckDto("adb-connection", "skip", "No authorized adb device.", null));
                return Build(request, ports.Count > 0
                        ? "Unisoc interfaces detected; no adb identity available."
                        : "No Unisoc device found.",
                    checks, log, warnings, errors, ports.Count > 0);
            }

            IReadOnlyList<Dictionary<string, string>> props = await _adb.GetPropertiesAsync(serial, ct);
            Dictionary<string, string> values = Flatten(props);

            (string Label, string Key)[] wanted =
            [
                ("Manufacturer", "ro.product.manufacturer"),
                ("Model", "ro.product.model"),
                ("Brand", "ro.product.brand"),
                ("Platform", "ro.board.platform"),
                ("Chip", "ro.hardware"),
                ("SoC", "ro.soc.model"),
                ("Android version", "ro.build.version.release"),
                ("Build", "ro.build.display.id"),
                ("Security patch", "ro.build.version.security_patch"),
                ("Bootloader", "ro.bootloader")
            ];

            log.Add("=== Unisoc device information ===");
            foreach ((string label, string key) in wanted)
            {
                string? value = values.GetValueOrDefault(key);
                log.Add($"{label,-18}: {(string.IsNullOrWhiteSpace(value) ? "—" : value)}");
                checks.Add(new DiagnosticCheckDto(key, string.IsNullOrWhiteSpace(value) ? "skip" : "pass",
                    string.IsNullOrWhiteSpace(value) ? $"{label} was not reported." : $"{label}: {value}", null));
            }

            string? manufacturer = values.GetValueOrDefault("ro.product.manufacturer");
            string? platform = values.GetValueOrDefault("ro.board.platform") ?? values.GetValueOrDefault("ro.hardware");

            bool isUnisoc = Contains(manufacturer, "spreadtrum", "unisoc", "sprd") ||
                            Contains(platform, "spd", "ums", "sc", "shark", "sprd", "uni");

            checks.Add(new DiagnosticCheckDto("spd-platform", platform is null ? "skip" : isUnisoc ? "pass" : "fail",
                platform is null ? "The board platform was not reported." : $"Board platform: {platform}", null));

            if (!isUnisoc)
            {
                warnings.Add($"The connected handset reports '{manufacturer ?? "unknown"}' / '{platform ?? "unknown"}', " +
                             "which does not look like a Unisoc platform; the SPD routines will refuse it.");
            }

            return Build(request, isUnisoc ? "Unisoc information read." : "The device does not look Unisoc based.",
                checks, log, warnings, errors, isUnisoc);
        }

        // ---------------------------------------------------------------- boot ROM

        /// <summary>
        /// Synchronises with the SPRD boot ROM. Opens the selected (or first BROM) port, sends the
        /// 0x7E synchronisation byte, and reports exactly what came back.
        /// </summary>
        public async Task<OperationExecutionResult> HandshakeAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            SpdPort? port = SelectPort(request.GetParameter("port"), SpdPortKind.Brom);
            if (port is null)
            {
                errors.Add("No SPRD boot-ROM interface is attached. Power the handset off (or hold the " +
                           "download-mode key combination) and connect USB; Windows must show the SPRD/Unisoc port.");
                checks.Add(new DiagnosticCheckDto("spd-port", "fail", errors[0], null));
                return Build(request, "No boot-ROM interface.", checks, log, warnings, errors);
            }

            if (!port.PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"The {port.KindLabel} interface has no COM name yet ({port.InstanceId}); wait for the driver to install.");
                return Build(request, "The port has no COM name.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("spd-port", "pass", port.Summary, null));

            try
            {
                await using ISpdTransport transport = _transports.Create(port.PortName);
                SpdBromSession session = new(transport, message => Log(request, log, message));
                SpdHandshakeResult result = await session.HandshakeAsync(3000, ct);

                checks.Add(new DiagnosticCheckDto("spd-handshake", result.Synchronised ? "pass" : "fail",
                    result.Detail, null));

                if (!result.Synchronised)
                {
                    errors.Add(result.Detail);
                    return Build(request, "The boot ROM did not answer.", checks, log, warnings, errors);
                }

                warnings.Add("Upload FDL1 next; the boot ROM does not expose storage until the download agent runs.");
                return Build(request, "Boot ROM synchronised.", checks, log, warnings, errors, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or TimeoutException or ArgumentException)
            {
                errors.Add(ex.Message);
                checks.Add(new DiagnosticCheckDto("spd-port-open", "fail", ex.Message, null));
                return Build(request, "The boot-ROM port could not be used.", checks, log, warnings, errors);
            }
        }

        /// <summary>
        /// Uploads the operator's FDL1 (and optionally FDL2) loader images over the synchronised
        /// boot ROM. Both paths come from parameters; nothing is bundled, because the loader must
        /// match the SoC revision.
        /// </summary>
        public async Task<OperationExecutionResult> UploadFdlAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            string? fdl1 = request.GetParameter("fdl1Path");
            string? fdl2 = request.GetParameter("fdl2Path");

            if (string.IsNullOrWhiteSpace(fdl1) || !File.Exists(fdl1))
            {
                errors.Add("Select the FDL1 loader image first (it must match the SoC).");
                checks.Add(new DiagnosticCheckDto("fdl1", "fail", errors[0], null));
                return Build(request, "No FDL1 image.", checks, log, warnings, errors);
            }

            if (!string.IsNullOrWhiteSpace(fdl2) && !File.Exists(fdl2))
            {
                errors.Add($"The FDL2 image '{fdl2}' does not exist.");
                return Build(request, "The FDL2 image was not found.", checks, log, warnings, errors);
            }

            SpdPort? port = SelectPort(request.GetParameter("port"), SpdPortKind.Brom);
            if (port is null || !port.PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("No SPRD boot-ROM COM port is available.");
                checks.Add(new DiagnosticCheckDto("spd-port", "fail", errors[0], null));
                return Build(request, "No boot-ROM port.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("spd-port", "pass", port.Summary, null));

            try
            {
                await using ISpdTransport transport = _transports.Create(port.PortName);
                SpdBromSession session = new(transport, message => Log(request, log, message));

                SpdHandshakeResult handshake = await session.HandshakeAsync(3000, ct);
                checks.Add(new DiagnosticCheckDto("spd-handshake", handshake.Synchronised ? "pass" : "fail",
                    handshake.Detail, null));

                if (!handshake.Synchronised)
                {
                    errors.Add(handshake.Detail);
                    return Build(request, "The boot ROM did not answer.", checks, log, warnings, errors);
                }

                byte[] image1 = await File.ReadAllBytesAsync(fdl1, ct);
                request.Process($"Uploading FDL1 {Path.GetFileName(fdl1)} ({image1.Length:N0} bytes).");
                long sent1 = await session.UploadAsync(image1, new StepProgress(request, "FDL1"), ct);

                checks.Add(new DiagnosticCheckDto("fdl1-upload", sent1 == image1.Length ? "pass" : "fail",
                    $"{sent1:N0} of {image1.Length:N0} byte(s) written.", null));

                if (sent1 != image1.Length)
                {
                    errors.Add("The FDL1 image was not written completely.");
                    return Build(request, "FDL1 upload incomplete.", checks, log, warnings, errors);
                }

                if (!string.IsNullOrWhiteSpace(fdl2))
                {
                    byte[] image2 = await File.ReadAllBytesAsync(fdl2, ct);
                    request.Process($"Uploading FDL2 {Path.GetFileName(fdl2)} ({image2.Length:N0} bytes).");
                    long sent2 = await session.UploadAsync(image2, new StepProgress(request, "FDL2"), ct);

                    checks.Add(new DiagnosticCheckDto("fdl2-upload", sent2 == image2.Length ? "pass" : "fail",
                        $"{sent2:N0} of {image2.Length:N0} byte(s) written.", null));

                    if (sent2 != image2.Length)
                    {
                        errors.Add("The FDL2 image was not written completely.");
                    }
                }
                else
                {
                    checks.Add(new DiagnosticCheckDto("fdl2-upload", "skip",
                        "Only FDL1 was supplied; the device stays in the download-agent stage.", null));
                }

                warnings.Add("The loader lives in RAM: unplugging the handset now loses it and the SoC returns to the boot ROM.");
                return Build(request, "Loader image(s) uploaded.", checks, log, warnings, errors, errors.Count == 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or TimeoutException or ArgumentException)
            {
                errors.Add(ex.Message);
                checks.Add(new DiagnosticCheckDto("spd-upload", "fail", ex.Message, null));
                return Build(request, "The loader upload failed.", checks, log, warnings, errors);
            }
        }

        // ---------------------------------------------------------------- fastboot routines

        /// <summary>Erases 'frp' and 'persistent' through fastboot, after an explicit confirmation.</summary>
        public Task<OperationExecutionResult> ResetFrpAsync(OperationExecutionRequest request, CancellationToken ct)
            => EraseAsync(request, "FRP reset", FrpPartitions, ct);

        /// <summary>Erases userdata and cache only; every calibration partition is refused by name.</summary>
        public Task<OperationExecutionResult> FactoryResetAsync(OperationExecutionRequest request, CancellationToken ct)
            => EraseAsync(request, "factory format", FormatPartitions, ct);

        /// <summary>Reboots the handset through fastboot (system, bootloader or recovery).</summary>
        public async Task<OperationExecutionResult> RebootAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            string target = (request.GetParameter("target") ?? "system").Trim().ToLowerInvariant();
            string? argument = target switch
            {
                "system" or "normal" => null,
                "bootloader" or "fastboot" => "bootloader",
                "recovery" => "recovery",
                "edl" => "edl",
                _ => null
            };

            if (argument is null && target is not ("system" or "normal"))
            {
                errors.Add($"'{target}' is not a supported reboot target for Unisoc handsets.");
                return Build(request, "Unsupported reboot target.", checks, log, warnings, errors);
            }

            string? fastboot = _tools.Find(ToolLocator.Fastboot);
            if (fastboot is null)
            {
                errors.Add("fastboot was not found. Install platform-tools next to the application.");
                return Build(request, "fastboot is unavailable.", checks, log, warnings, errors);
            }

            List<string> arguments = argument is null ? ["reboot"] : ["reboot", argument];
            ProcessResult result = await _runner.RunAsync(
                new ProcessRequest(fastboot, arguments, TimeoutSeconds: 60), ct);

            log.Add($"fastboot {string.Join(' ', arguments)} -> exit {result.ExitCode?.ToString() ?? "?"}");
            checks.Add(new DiagnosticCheckDto("spd-reboot", result.Succeeded ? "pass" : "fail",
                result.Succeeded ? $"Rebooted to {target}." : FirstLine(result.StandardError), null));

            if (!result.Succeeded)
            {
                errors.Add($"fastboot reboot failed: {FirstLine(result.StandardError)}");
            }

            return Build(request, result.Succeeded ? $"Reboot to {target} sent." : "The reboot was not accepted.",
                checks, log, warnings, errors);
        }

        private async Task<OperationExecutionResult> EraseAsync(OperationExecutionRequest request, string routine,
            IReadOnlyList<string> partitions, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{routine}' has to be confirmed explicitly: it erases partitions. Confirm in the client and retry.");
                return Build(request, "Confirmation required.", checks, log, warnings, errors);
            }

            string? requested = request.GetParameter("partition");
            if (!string.IsNullOrWhiteSpace(requested) && IsProtected(requested))
            {
                errors.Add($"'{requested}' carries calibration data and is never erased by this module.");
                checks.Add(new DiagnosticCheckDto("protected-partition", "fail", errors[0], null));
                return Build(request, $"{routine} refused.", checks, log, warnings, errors);
            }

            string? fastboot = _tools.Find(ToolLocator.Fastboot);
            if (fastboot is null)
            {
                errors.Add("fastboot was not found. Install platform-tools next to the application.");
                return Build(request, "fastboot is unavailable.", checks, log, warnings, errors);
            }

            ProcessResult devices = await _runner.RunAsync(new ProcessRequest(fastboot, ["devices"], TimeoutSeconds: 30), ct);
            string connected = devices.StandardOutput.Trim();
            checks.Add(new DiagnosticCheckDto("fastboot-device", string.IsNullOrWhiteSpace(connected) ? "fail" : "pass",
                string.IsNullOrWhiteSpace(connected)
                    ? "No fastboot device answered. Boot the handset into fastboot mode and retry."
                    : $"fastboot device: {Truncate(connected.Replace(Environment.NewLine, " "), 160)}",
                null));

            if (string.IsNullOrWhiteSpace(connected))
            {
                errors.Add($"The Unisoc {routine} needs the handset in fastboot mode.");
                return Build(request, "No fastboot device.", checks, log, warnings, errors);
            }

            bool allSucceeded = true;
            foreach (string partition in partitions)
            {
                if (IsProtected(partition))
                {
                    // Belt and braces: the fixed list never contains one, but a renamed table must not slip through.
                    checks.Add(new DiagnosticCheckDto($"erase-{partition}", "fail",
                        $"'{partition}' is a calibration partition and was refused.", null));
                    allSucceeded = false;
                    continue;
                }

                request.Process($"fastboot erase {partition}");
                ProcessResult erase = await _runner.RunAsync(
                    new ProcessRequest(fastboot, ["erase", partition], TimeoutSeconds: 240), ct);

                allSucceeded &= erase.Succeeded;
                string output = FirstLine(erase.StandardOutput + " " + erase.StandardError);
                log.Add($"fastboot erase {partition} -> exit {erase.ExitCode?.ToString() ?? "?"}: {Truncate(output, 200)}");
                checks.Add(new DiagnosticCheckDto($"erase-{partition}", erase.Succeeded ? "pass" : "fail",
                    erase.Succeeded ? $"Partition '{partition}' erased." : $"'{partition}' failed: {Truncate(output, 200)}",
                    null));

                if (!erase.Succeeded)
                {
                    errors.Add($"fastboot erase {partition} failed: {Truncate(output, 200)}");
                }
            }

            checks.Add(new DiagnosticCheckDto("calibration-safeguard", "pass",
                "persist, prodnv, nvram, nvdata, modem and the other calibration partitions were never targeted.", null));
            warnings.Add("Reboot the handset after the routine and confirm that setup completes normally.");

            return Build(request, allSucceeded ? $"Unisoc {routine} completed." : $"Unisoc {routine} finished with errors.",
                checks, log, warnings, errors);
        }

        // ---------------------------------------------------------------- helpers

        private static IReadOnlyList<SpdPort> DefaultPorts()
            => OperatingSystem.IsWindows() ? SpdPortLocator.FindPorts() : Array.Empty<SpdPort>();

        private IReadOnlyList<SpdPort> ListPorts()
        {
            try
            {
                return _portEnumerator();
            }
            catch (Exception)
            {
                return Array.Empty<SpdPort>();
            }
        }

        /// <summary>Explicit port parameter first, then the first interface of the requested kind.</summary>
        private SpdPort? SelectPort(string? requested, SpdPortKind preferred)
        {
            IReadOnlyList<SpdPort> ports = ListPorts();

            if (!string.IsNullOrWhiteSpace(requested))
            {
                SpdPort? exact = ports.FirstOrDefault(p =>
                    string.Equals(p.PortName, requested, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.InstanceId, requested, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    return exact;
                }

                return requested.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    ? new SpdPort(requested, requested, requested, preferred, null, null)
                    : null;
            }

            return ports.FirstOrDefault(p => p.Kind == preferred)
                ?? ports.FirstOrDefault(p => p.IsLoaderStage);
        }

        private async Task<string?> ResolveSerialAsync(string? preferred, CancellationToken ct)
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

        private static bool Contains(string? value, params string[] hints)
            => value is not null && hints.Any(h => value.Contains(h, StringComparison.OrdinalIgnoreCase));

        private static string FirstLine(string value)
            => value.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value[..max] + "…";

        private static void Log(OperationExecutionRequest request, List<string> log, string message)
        {
            log.Add(message);
            request.Process(message);
        }

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

        /// <summary>Reports a transfer at each tenth so a long upload does not flood the console.</summary>
        private sealed class StepProgress : IProgress<double>
        {
            private readonly OperationExecutionRequest _request;
            private readonly string _label;
            private int _lastBucket = -1;

            public StepProgress(OperationExecutionRequest request, string label)
            {
                _request = request;
                _label = label;
            }

            public void Report(double value)
            {
                int bucket = Math.Clamp((int)Math.Floor(value / 10d) * 10, 0, 100);
                if (bucket == _lastBucket)
                {
                    return;
                }

                _lastBucket = bucket;
                _request.Process($"{_label}: {bucket}%");
            }
        }
    }

    /// <summary>Routes the SPD operation types to the Unisoc engine.</summary>
    public sealed class SpdModuleExecutor : IOperationExecutor
    {
        private readonly SpdExecutionService _service;

        public SpdModuleExecutor(IAdbClient adb, IToolLocator tools, IProcessRunner runner,
            ISpdTransportFactory? transports = null,
            Func<IReadOnlyList<SpdPort>>? portEnumerator = null)
        {
            _service = new SpdExecutionService(adb, tools, runner, transports, portEnumerator);
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.SpdReadInfo,
            OperationTypes.SpdHandshake,
            OperationTypes.SpdFdl,
            OperationTypes.SpdResetFrp,
            OperationTypes.SpdFactoryReset,
            OperationTypes.SpdReboot
        ];

        public Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
            => request.OperationType switch
            {
                OperationTypes.SpdReadInfo => _service.ReadInfoAsync(request, ct),
                OperationTypes.SpdHandshake => _service.HandshakeAsync(request, ct),
                OperationTypes.SpdFdl => _service.UploadFdlAsync(request, ct),
                OperationTypes.SpdResetFrp => _service.ResetFrpAsync(request, ct),
                OperationTypes.SpdFactoryReset => _service.FactoryResetAsync(request, ct),
                OperationTypes.SpdReboot => _service.RebootAsync(request, ct),
                _ => Task.FromResult(OperationExecutionResult.Failure(request.OperationType,
                    "Unsupported operation.", $"The Unisoc module does not handle '{request.OperationType}'."))
            };
    }
}
