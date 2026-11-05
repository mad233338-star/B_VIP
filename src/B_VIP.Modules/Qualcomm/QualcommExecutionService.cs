using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.Qualcomm
{
    /// <summary>
    /// Qualcomm emergency-download (9008) engine.
    ///
    /// What it really does: enumerates the 9008 interfaces Windows reports, speaks the Sahara handshake
    /// and streams the operator's programmer (prog_firehose_*.elf / .mbn) to the boot ROM, then drives
    /// the running programmer over Firehose to read the GPT, erase a verified partition boundary, or
    /// power-cycle the handset.
    ///
    /// What it needs from the operator: the programmer binary that matches the SoC. Without it the
    /// boot ROM will not hand over the storage controller, so no partition can be read or erased. Every
    /// destructive routine re-reads the GPT first and refuses calibration partitions outright.
    /// </summary>
    public sealed class QualcommExecutionService
    {
        /// <summary>Sector size assumed for the partition table unless the device needs 512 byte sectors.</summary>
        public const int DefaultSectorSize = 4096;

        /// <summary>Sectors read to cover LBA 0 plus the primary GPT header and its 128 entry slots.</summary>
        public const int GptSectorCount = 34;

        /// <summary>Partition names a factory reset may erase, in order.</summary>
        private static readonly string[] ResetPartitions = ["userdata", "cache"];

        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;
        private readonly IEdlTransportFactory _transports;
        private readonly Func<IReadOnlyList<QualcommPort>> _portEnumerator;

        public QualcommExecutionService(IAdbClient adb, IToolLocator tools,
            IEdlTransportFactory? transports = null,
            Func<IReadOnlyList<QualcommPort>>? portEnumerator = null)
        {
            _adb = adb;
            _tools = tools;
            _transports = transports ?? new SerialEdlTransportFactory();
            _portEnumerator = portEnumerator ?? DefaultPorts;
        }

        /// <summary>
        /// Reads the platform identity over adb when a handset is running Android, and always reports
        /// the 9008 interfaces Windows currently sees, so the operator knows whether the SoC is in EDL.
        /// </summary>
        public async Task<OperationExecutionResult> ReadInfoAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Info("Step 1/2 — enumerating Qualcomm USB interfaces.");
            IReadOnlyList<QualcommPort> ports = ListPorts();

            foreach (QualcommPort port in ports.Where(p => p.IsEmergencyDownload))
            {
                request.Info($"Qualcomm 9008 port detected on {port.PortName} ({port.UsbId}).");
            }

            checks.Add(new DiagnosticCheckDto("qualcomm-interfaces", ports.Count > 0 ? "pass" : "skip",
                ports.Count > 0
                    ? string.Join("; ", ports.Select(p => p.Summary))
                    : "No Qualcomm interface is attached.",
                null));

            bool edl = ports.Any(p => p.IsEmergencyDownload);
            checks.Add(new DiagnosticCheckDto("qualcomm-edl", edl ? "pass" : "warn",
                edl
                    ? "A device is in emergency download mode and can accept a programmer."
                    : "No 9008 interface: the handset is either off, in Android, or the QDLoader driver is missing.",
                null));

            if (!edl && ports.Count == 0)
            {
                warnings.Add("Power the handset off, hold the EDL key combination (or short the test points on older boards) " +
                             "and connect USB. Windows must install the Qualcomm HS-USB QDLoader 9008 driver.");
            }

            request.Info("Step 2/2 — reading platform properties through adb.");
            string? serial = await ResolveAdbSerialAsync(request.Serial, ct);
            if (serial is null)
            {
                warnings.Add("No authorized adb connection, so the SoC, build and bootloader identity could not be read.");
                checks.Add(new DiagnosticCheckDto("adb-connection", "skip", "No authorized adb device.", null));

                if (!edl)
                {
                    errors.Add("Neither a Qualcomm 9008 interface nor an adb device was found.");
                }

                return Build(request,
                    edl ? "Qualcomm EDL interface detected; no adb identity available." : "No Qualcomm device found.",
                    checks, log, warnings, errors, edl);
            }

            IReadOnlyList<Dictionary<string, string>> props = await _adb.GetPropertiesAsync(serial, ct);
            Dictionary<string, string> values = Flatten(props);

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
                ("Bootloader state", "ro.boot.vbmeta.device_state"),
                ("Slot", "ro.boot.slot_suffix")
            ];

            log.Add("=== Qualcomm device information ===");
            foreach ((string label, string key) in wanted)
            {
                string? value = values.GetValueOrDefault(key);
                log.Add($"{label,-18}: {(string.IsNullOrWhiteSpace(value) ? "—" : value)}");
                checks.Add(new DiagnosticCheckDto(key, string.IsNullOrWhiteSpace(value) ? "skip" : "pass",
                    string.IsNullOrWhiteSpace(value) ? $"{label} was not reported." : $"{label}: {value}", null));
            }

            string? platform = values.GetValueOrDefault("ro.board.platform")
                ?? values.GetValueOrDefault("ro.hardware")
                ?? values.GetValueOrDefault("ro.soc.model");

            bool isQualcomm = platform is not null &&
                (platform.StartsWith("msm", StringComparison.OrdinalIgnoreCase) ||
                 platform.StartsWith("sdm", StringComparison.OrdinalIgnoreCase) ||
                 platform.StartsWith("sm", StringComparison.OrdinalIgnoreCase) ||
                 platform.StartsWith("kona", StringComparison.OrdinalIgnoreCase) ||
                 platform.StartsWith("lahaina", StringComparison.OrdinalIgnoreCase) ||
                 platform.StartsWith("taro", StringComparison.OrdinalIgnoreCase) ||
                 platform.Contains("qcom", StringComparison.OrdinalIgnoreCase));

            checks.Add(new DiagnosticCheckDto("qualcomm-platform", platform is null ? "skip" : isQualcomm ? "pass" : "fail",
                platform is null ? "Board platform was not reported." : $"Board platform: {platform}", null));

            if (!isQualcomm)
            {
                warnings.Add($"The connected handset reports platform '{platform ?? "unknown"}'; the Qualcomm routines will refuse it.");
            }

            return Build(request, isQualcomm ? "Qualcomm information read." : "The device does not look Qualcomm based.",
                checks, log, warnings, errors, isQualcomm);
        }

        /// <summary>
        /// Sahara stage: opens the 9008 port, answers the boot ROM's HELLO and streams the programmer the
        /// operator selected. Success means the device acknowledged DONE for that image.
        /// </summary>
        public async Task<OperationExecutionResult> SaharaAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            string? programmerPath = request.GetParameter("firehosePath") ?? request.GetParameter("path");
            if (string.IsNullOrWhiteSpace(programmerPath))
            {
                errors.Add("Select a programmer binary (prog_firehose_*.elf / prog_firehose_*.mbn) for this SoC first.");
                return Build(request, "No programmer selected.", checks, log, warnings, errors);
            }

            if (!File.Exists(programmerPath))
            {
                errors.Add($"The programmer '{programmerPath}' does not exist.");
                return Build(request, "Programmer not found.", checks, log, warnings, errors);
            }

            request.Process($"Programmer: {Path.GetFileName(programmerPath)} ({new FileInfo(programmerPath).Length:N0} bytes)");

            IEdlTransport transport = await OpenTransportAsync(request, checks, ct);
            await using (transport)
            {
                SaharaSession session = new(transport, message => Log(request, log, message));

                try
                {
                    request.Process($"Sending the Sahara HELLO response on {transport.Description}.");
                    SaharaHello hello = await session.HandshakeAsync(ct);
                    checks.Add(new DiagnosticCheckDto("sahara-hello", "pass",
                        $"HELLO accepted: protocol {hello.Version}.{hello.MinimumVersion}, device mode 0x{hello.Mode:X2}.", null));

                    byte[] programmer = await File.ReadAllBytesAsync(programmerPath, ct);
                    request.Process("Uploading the programmer.");
                    SaharaUploadResult upload = await session.UploadAsync(programmer,
                        new StepProgress(request, "Programmer transfer"), ct);

                    checks.Add(new DiagnosticCheckDto("sahara-upload", upload.Uploaded ? "pass" : "fail",
                        $"{upload.BytesSent:N0} byte(s) transferred; device acknowledged DONE.", null));

                    if (!upload.Uploaded)
                    {
                        errors.Add(upload.Detail);
                    }
                    else
                    {
                        request.Success($"Programmer accepted on {transport.Description}.");
                        warnings.Add("The handset normally re-enumerates after the programmer starts. If a Firehose " +
                                     "command now fails, pick the port that appeared and run the command again.");
                    }

                    return Build(request,
                        upload.Uploaded ? "Sahara handshake and programmer upload completed." : upload.Detail,
                        checks, log, warnings, errors, upload.Uploaded);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException
                    or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add(ex.Message);
                    checks.Add(new DiagnosticCheckDto("sahara-handshake", "fail", ex.Message, null));
                    return Build(request, "The Sahara stage failed.", checks, log, warnings, errors);
                }
            }
        }

        /// <summary>Reads and parses the GPT through Firehose, so the partition layout is known before anything is erased.</summary>
        public async Task<OperationExecutionResult> GptAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            await using EdlSession? session = await ConnectAsync(request, checks, log, warnings, errors, ct);
            if (session is null)
            {
                return Build(request, "The GPT could not be read.", checks, log, warnings, errors);
            }

            GptTable? table = await ReadGptAsync(session, request, checks, log, warnings, errors, ct);
            if (table is null)
            {
                return Build(request, "The GPT could not be read.", checks, log, warnings, errors);
            }

            return Build(request, $"Partition table read: {table.Partitions.Count} partition(s).",
                checks, log, warnings, errors, true);
        }

        /// <summary>
        /// Resets FRP by reading the partition table first, locating 'frp' (or 'config' on older
        /// layouts) and erasing exactly that partition's sector range.
        /// </summary>
        public async Task<OperationExecutionResult> ResetFrpAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (!IsConfirmed(request))
            {
                return OperationExecutionResult.Skipped(request.OperationType,
                    "The FRP reset was not confirmed.",
                    "The operator did not confirm the FRP reset, so nothing was erased.");
            }

            await using EdlSession? session = await ConnectAsync(request, checks, log, warnings, errors, ct);
            if (session is null)
            {
                return Build(request, "The FRP reset could not start.", checks, log, warnings, errors);
            }

            GptTable? table = await ReadGptAsync(session, request, checks, log, warnings, errors, ct);
            if (table is null)
            {
                return Build(request, "The FRP reset stopped: the partition table could not be read.",
                    checks, log, warnings, errors);
            }

            GptPartition? frp = table.Find("frp", "config", "frp_p");
            if (frp is null)
            {
                errors.Add("This partition table declares no 'frp' or 'config' partition, so there is nothing to erase.");
                checks.Add(new DiagnosticCheckDto("frp-partition", "fail", errors[0], null));
                return Build(request, "The FRP partition was not found.", checks, log, warnings, errors);
            }

            // A table that names a calibration partition as FRP is refused rather than trusted.
            if (GptTable.IsProtected(frp.Name))
            {
                errors.Add($"'{frp.Name}' carries calibration data and is never erased by this module.");
                checks.Add(new DiagnosticCheckDto("frp-protected", "fail", errors[0], null));
                return Build(request, "FRP reset refused: the matched partition is protected.",
                    checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("frp-partition", "pass",
                $"'{frp.Name}' at LBA {frp.FirstLba}..{frp.LastLba} ({frp.SizeText(table.SectorSize)}).", null));

            request.Process($"Erasing '{frp.Name}' ({frp.SectorCount} sectors at LBA {frp.FirstLba}).");
            FirehoseResponse erase = await session.Firehose.SendAsync(
                FirehoseCommands.Erase(0, frp.FirstLba, frp.SectorCount, table.SectorSize), ct);

            checks.Add(new DiagnosticCheckDto("frp-erase", erase.Acknowledged ? "pass" : "fail",
                erase.Acknowledged
                    ? $"'{frp.Name}' erased."
                    : erase.Error ?? "The programmer did not acknowledge the erase.",
                null));

            if (!erase.Acknowledged)
            {
                errors.Add(erase.Error ?? $"The programmer refused to erase '{frp.Name}'.");
                return Build(request, "The FRP partition was not erased.", checks, log, warnings, errors);
            }

            request.Success($"FRP data erased from '{frp.Name}'.");
            warnings.Add("Power-cycle the handset and complete setup without adding a Google account.");
            return Build(request, $"FRP erased on '{frp.Name}'.", checks, log, warnings, errors, true);
        }

        /// <summary>Wipes userdata and cache only, refusing every calibration partition by name.</summary>
        public async Task<OperationExecutionResult> FactoryResetAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (!IsConfirmed(request))
            {
                return OperationExecutionResult.Skipped(request.OperationType,
                    "The factory reset was not confirmed.",
                    "The operator did not confirm the factory reset, so nothing was erased.");
            }

            string? requested = request.GetParameter("partition");
            if (!string.IsNullOrWhiteSpace(requested) && GptTable.IsProtected(requested))
            {
                errors.Add($"'{requested}' carries calibration or boot data and is never erased by this module.");
                checks.Add(new DiagnosticCheckDto("protected-partition", "fail", errors[0], null));
                return Build(request, "Factory reset refused.", checks, log, warnings, errors);
            }

            await using EdlSession? session = await ConnectAsync(request, checks, log, warnings, errors, ct);
            if (session is null)
            {
                return Build(request, "The factory reset could not start.", checks, log, warnings, errors);
            }

            GptTable? table = await ReadGptAsync(session, request, checks, log, warnings, errors, ct);
            if (table is null)
            {
                return Build(request, "The factory reset stopped: the partition table could not be read.",
                    checks, log, warnings, errors);
            }

            List<GptPartition> targets = new();
            foreach (string name in ResetPartitions)
            {
                GptPartition? partition = table.Find(name);
                if (partition is null)
                {
                    checks.Add(new DiagnosticCheckDto($"partition-{name}", "warn",
                        $"This table declares no '{name}' partition; skipped.", null));
                    continue;
                }

                if (GptTable.IsProtected(partition.Name))
                {
                    // Belt and braces: a table that renames a calibration partition is still refused.
                    checks.Add(new DiagnosticCheckDto($"protect-{name}", "fail",
                        $"'{partition.Name}' is a calibration partition and was refused.", null));
                    continue;
                }

                targets.Add(partition);
            }

            if (targets.Count == 0)
            {
                errors.Add("None of the partitions this format may erase exist in the table.");
                return Build(request, "Nothing to erase.", checks, log, warnings, errors);
            }

            foreach (GptPartition partition in targets)
            {
                request.Process($"Erasing '{partition.Name}' ({partition.SectorCount} sectors at LBA {partition.FirstLba}).");
                FirehoseResponse erase = await session.Firehose.SendAsync(
                    FirehoseCommands.Erase(0, partition.FirstLba, partition.SectorCount, table.SectorSize), ct);

                checks.Add(new DiagnosticCheckDto($"erase-{partition.Name}", erase.Acknowledged ? "pass" : "fail",
                    erase.Acknowledged
                        ? $"'{partition.Name}' erased ({partition.SizeText(table.SectorSize)})."
                        : erase.Error ?? "The programmer did not acknowledge the erase.",
                    null));

                if (!erase.Acknowledged)
                {
                    errors.Add($"{partition.Name}: {erase.Error ?? "erase refused"}");
                }
            }

            string protectedList = string.Join(", ", GptTable.ProtectedPartitionNames.Take(4)) + " …";
            checks.Add(new DiagnosticCheckDto("calibration-safeguard", "pass",
                $"modemst1, modemst2, fsg and persist were never targeted ({protectedList}).", null));
            warnings.Add("A factory reset removes user data; on a protected handset run the FRP reset as well.");

            bool succeeded = errors.Count == 0;
            return Build(request, succeeded ? "Factory format completed." : errors[0],
                checks, log, warnings, errors, succeeded);
        }

        /// <summary>Sends an EDL power command: <c>reset</c> (normal boot), <c>off</c> or <c>edl</c>.</summary>
        public async Task<OperationExecutionResult> RebootAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            string target = (request.GetParameter("target") ?? "reset").Trim().ToLowerInvariant();
            if (target is not ("reset" or "system" or "normal" or "off" or "edl"))
            {
                errors.Add($"'{target}' is not a supported EDL power target.");
                return Build(request, "Unsupported reboot target.", checks, log, warnings, errors);
            }

            string value = target switch
            {
                "off" => "off",
                "edl" => "edl",
                _ => "reset"
            };

            await using EdlSession? session = await ConnectAsync(request, checks, log, warnings, errors, ct);
            if (session is null)
            {
                return Build(request, "The reboot could not be sent.", checks, log, warnings, errors);
            }

            request.Process($"Sending the EDL power command '{value}'.");
            FirehoseResponse power = await session.Firehose.SendAsync(FirehoseCommands.Power(value), ct);

            checks.Add(new DiagnosticCheckDto("edl-power", power.Acknowledged || power.Rejected ? "pass" : "warn",
                power.Acknowledged
                    ? $"The programmer accepted power '{value}'."
                    : power.Error ?? "The programmer did not answer; the device may already be rebooting.",
                null));

            bool succeeded = power.Acknowledged || power.Rejected;
            warnings.Add("The device leaves EDL when the power command lands; it reappears in Android or fastboot.");
            return Build(request, succeeded ? $"Power '{value}' sent." : "The power command was not acknowledged.",
                checks, log, warnings, errors, succeeded);
        }

        // ---------------------------------------------------------------- session

        /// <summary>One open EDL link: the transport, the running programmer and the Sahara greeting.</summary>
        private sealed class EdlSession : IAsyncDisposable
        {
            public EdlSession(IEdlTransport transport, FirehoseClient firehose, SaharaHello hello, long programmerBytes)
            {
                Transport = transport;
                Firehose = firehose;
                Hello = hello;
                ProgrammerBytes = programmerBytes;
            }

            public IEdlTransport Transport { get; }

            public FirehoseClient Firehose { get; }

            public SaharaHello Hello { get; }

            public long ProgrammerBytes { get; }

            public ValueTask DisposeAsync() => Transport.DisposeAsync();
        }

        /// <summary>
        /// Opens the port, answers HELLO, uploads the selected programmer and configures Firehose. Every
        /// failure is reported as a check so the operator sees exactly which stage stopped.
        /// </summary>
        private async Task<EdlSession?> ConnectAsync(OperationExecutionRequest request, List<DiagnosticCheckDto> checks,
            List<string> log, List<string> warnings, List<string> errors, CancellationToken ct)
        {
            string? firehosePort = request.GetParameter("firehosePort");

            bool handedOver = false;
            IEdlTransport transport = await OpenTransportAsync(request, checks, ct);

            try
            {
                SaharaSession sahara = new(transport, message => Log(request, log, message));

                string? programmerPath = request.GetParameter("firehosePath") ?? request.GetParameter("path");
                if (string.IsNullOrWhiteSpace(programmerPath))
                {
                    errors.Add("This routine reads and writes the storage controller, which needs the programmer " +
                               "binary for this SoC. Select a prog_firehose_*.elf / .mbn file first.");
                    return null;
                }

                if (!File.Exists(programmerPath))
                {
                    errors.Add($"The programmer '{programmerPath}' does not exist.");
                    return null;
                }

                request.Process($"Sending the Sahara HELLO response on {transport.Description}.");
                SaharaHello hello = await sahara.HandshakeAsync(ct);
                checks.Add(new DiagnosticCheckDto("sahara-hello", "pass",
                    $"HELLO accepted: protocol {hello.Version}.{hello.MinimumVersion}.", null));

                byte[] programmer = await File.ReadAllBytesAsync(programmerPath, ct);
                request.Process($"Uploading {Path.GetFileName(programmerPath)} ({programmer.Length:N0} bytes).");
                SaharaUploadResult upload = await sahara.UploadAsync(programmer,
                    new StepProgress(request, "Programmer transfer"), ct);

                checks.Add(new DiagnosticCheckDto("sahara-upload", upload.Uploaded ? "pass" : "fail",
                    upload.Detail, null));

                if (!upload.Uploaded)
                {
                    errors.Add(upload.Detail);
                    return null;
                }

                IEdlTransport firehoseTransport = transport;
                if (!string.IsNullOrWhiteSpace(firehosePort) &&
                    !string.Equals(firehosePort, transport.Description, StringComparison.OrdinalIgnoreCase))
                {
                    request.Process($"Re-opening Firehose on {firehosePort} after the programmer started.");
                    firehoseTransport = _transports.Create(firehosePort.Trim());
                    await firehoseTransport.OpenAsync(ct);
                    handedOver = true;
                }

                FirehoseClient firehose = new(firehoseTransport, message => Log(request, log, message));

                FirehoseResponse configure = await firehose.SendAsync(FirehoseCommands.Configure(), ct);
                checks.Add(new DiagnosticCheckDto("firehose-configure", configure.Acknowledged ? "pass" : "fail",
                    configure.Acknowledged
                        ? "Firehose configured."
                        : configure.Error ?? "The programmer did not acknowledge the configure command.",
                    null));

                if (!configure.Acknowledged)
                {
                    errors.Add(configure.Error ?? "Firehose did not accept the configure command.");
                    if (!handedOver)
                    {
                        warnings.Add("A 9008 device normally re-enumerates once the programmer starts. If it does, " +
                                     "select the port that appeared as the Firehose port and run the command again.");
                    }

                    await firehoseTransport.DisposeAsync();
                    handedOver = true;
                    return null;
                }

                request.Success("Firehose link ready.");
                return new EdlSession(firehoseTransport, firehose, hello, programmer.LongLength);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add(ex.Message);
                checks.Add(new DiagnosticCheckDto("edl-link", "fail", ex.Message, null));
                return null;
            }
            finally
            {
                if (!handedOver)
                {
                    await transport.DisposeAsync();
                }
            }
        }

        /// <summary>Chooses the port (explicit parameter, else the first 9008 interface) and opens it.</summary>
        private async Task<IEdlTransport> OpenTransportAsync(OperationExecutionRequest request,
            List<DiagnosticCheckDto> checks, CancellationToken ct)
        {
            IReadOnlyList<QualcommPort> ports = ListPorts();
            string? requested = request.GetParameter("port");

            QualcommPort? port = requested is not null
                ? ports.FirstOrDefault(p => string.Equals(p.PortName, requested, StringComparison.OrdinalIgnoreCase))
                  ?? new QualcommPort(requested, requested, requested, QualcommPortKind.Edl9008, null, null)
                : ports.FirstOrDefault(p => p.IsEmergencyDownload && p.PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase));

            if (port is null)
            {
                throw new InvalidOperationException(
                    "No Qualcomm 9008 interface is attached. Enter emergency download mode and connect USB; " +
                    "Windows must show the Qualcomm HS-USB QDLoader 9008 port.");
            }

            if (port.PortName.StartsWith("(", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The {port.KindLabel} interface has no COM name yet ({port.InstanceId}); wait for the driver to finish installing.");
            }

            checks.Add(new DiagnosticCheckDto("edl-port", "pass", port.Summary, null));
            request.Info($"Qualcomm 9008 port detected on {port.PortName} ({port.UsbId}).");

            IEdlTransport transport = _transports.Create(port.PortName);
            await transport.OpenAsync(ct);
            request.Process($"Opened {port.PortName} at {SerialEdlTransport.BaudRate} baud.");
            return transport;
        }

        /// <summary>Reads LBA 0..33 and parses the table, falling back to the other sector size once.</summary>
        private async Task<GptTable?> ReadGptAsync(EdlSession session, OperationExecutionRequest request,
            List<DiagnosticCheckDto> checks, List<string> log, List<string> warnings, List<string> errors,
            CancellationToken ct)
        {
            int requested = ParseInt(request.GetParameter("sectorSize")) ?? DefaultSectorSize;
            int[] attempts = requested == 512 ? [512, 4096] : [requested, 512];

            foreach (int sectorSize in attempts.Distinct())
            {
                try
                {
                    request.Process($"Reading LBA 0..{GptSectorCount - 1} with a {sectorSize} byte sector size.");
                    byte[] block = await session.Firehose.ReadSectorsAsync(0, 0, GptSectorCount, sectorSize, ct);

                    GptTable table = GptTable.Parse(block, sectorSize);
                    checks.Add(new DiagnosticCheckDto("gpt-read", "pass",
                        $"{table.Partitions.Count} partition(s) parsed with {sectorSize} byte sectors.", null));

                    log.Add($"=== GPT: {table.Partitions.Count} partitions, disk {table.DiskGuid} ===");
                    foreach (GptPartition partition in table.Partitions)
                    {
                        log.Add(partition.Describe(sectorSize));
                    }

                    int protectedCount = table.Partitions.Count(p => GptTable.IsProtected(p.Name));
                    checks.Add(new DiagnosticCheckDto("gpt-protected", "pass",
                        $"{protectedCount} partition(s) are treated as calibration data and never erased.", null));

                    return table;
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or TimeoutException or IOException)
                {
                    checks.Add(new DiagnosticCheckDto($"gpt-read-{sectorSize}", "warn",
                        $"{sectorSize} byte sectors: {ex.Message}", null));
                    log.Add($"GPT read with {sectorSize} byte sectors failed: {ex.Message}");
                }
            }

            errors.Add("The partition table could not be read or parsed with either supported sector size.");
            warnings.Add("Verify the programmer matches this SoC and that the device stayed in EDL for the whole read.");
            return null;
        }

        // ---------------------------------------------------------------- helpers

        private static IReadOnlyList<QualcommPort> DefaultPorts()
            => OperatingSystem.IsWindows() ? QualcommPortLocator.FindPorts() : Array.Empty<QualcommPort>();

        private IReadOnlyList<QualcommPort> ListPorts()
        {
            try
            {
                return _portEnumerator();
            }
            catch (Exception)
            {
                // Enumeration failures surface as "no interface"; the caller explains what to check.
                return Array.Empty<QualcommPort>();
            }
        }

        private static bool IsConfirmed(OperationExecutionRequest request)
            => string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase);

        private static void Log(OperationExecutionRequest request, List<string> log, string message)
        {
            log.Add(message);
            request.Process(message);
        }

        /// <summary>Reports transfer progress at each tenth, so the console carries it without a line per packet.</summary>
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

        private static int? ParseInt(string? value)
            => int.TryParse(value, out int parsed) ? parsed : null;

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

    /// <summary>Routes the Qualcomm operation types to the EDL engine, and the diagnostic to the adb module.</summary>
    public sealed class QualcommEdlExecutor : IOperationExecutor
    {
        private readonly QualcommExecutionService _edl;
        private readonly global::B_VIP.Modules.QualcommModuleExecutor _diagnostics;

        public QualcommEdlExecutor(IAdbClient adb, IToolLocator tools,
            IEdlTransportFactory? transports = null,
            Func<IReadOnlyList<QualcommPort>>? portEnumerator = null)
        {
            _edl = new QualcommExecutionService(adb, tools, transports, portEnumerator);
            _diagnostics = new global::B_VIP.Modules.QualcommModuleExecutor(adb, tools);
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.QualcommDiagnostic,
            OperationTypes.QualcommReadInfo,
            OperationTypes.QualcommSahara,
            OperationTypes.QualcommGpt,
            OperationTypes.QualcommResetFrp,
            OperationTypes.QualcommFactoryReset,
            OperationTypes.QualcommReboot
        ];

        public Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
            => request.OperationType switch
            {
                OperationTypes.QualcommReadInfo => _edl.ReadInfoAsync(request, ct),
                OperationTypes.QualcommSahara => _edl.SaharaAsync(request, ct),
                OperationTypes.QualcommGpt => _edl.GptAsync(request, ct),
                OperationTypes.QualcommResetFrp => _edl.ResetFrpAsync(request, ct),
                OperationTypes.QualcommFactoryReset => _edl.FactoryResetAsync(request, ct),
                OperationTypes.QualcommReboot => _edl.RebootAsync(request, ct),
                // The original diagnostic keeps reading the platform over adb and is unchanged.
                _ => _diagnostics.ExecuteAsync(request, ct)
            };
    }
}
