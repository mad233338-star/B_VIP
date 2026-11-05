using System.Text.Json;
using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>
    /// Everything a brand module needs: how the handset identifies itself, which operations it
    /// exposes and which fastboot partitions belong to each maintenance routine.
    /// </summary>
    public sealed record BrandProfile(
        string Key,
        string DisplayName,
        string ReadInfoOperation,
        string FrpOperation,
        string? WipeOperation,
        string[] ManufacturerHints,
        string[] PlatformHints,
        string[] ProductHints,
        string[] FrpPartitions,
        string[] WipePartitions,
        bool WipeIsFactoryFormat);

    public static class BrandProfiles
    {
        public static readonly BrandProfile Xiaomi = new(
            "xiaomi", "Xiaomi",
            OperationTypes.XiaomiReadInfo, OperationTypes.XiaomiResetFrp, null,
            ["xiaomi", "redmi", "poco"],
            ["sm", "msm", "sdm", "mt", "taro", "lahaina", "kona"],
            ["xiaomi", "redmi", "poco", "mi"],
            ["frp", "config"],
            [],
            false);

        public static readonly BrandProfile Infinix = new(
            "infinix", "Infinix",
            OperationTypes.InfinixReadInfo, OperationTypes.InfinixResetFrp, OperationTypes.InfinixFactoryFormat,
            ["infinix"],
            ["mt", "sm", "ums", "sc"],
            ["infinix", "x6", "x7", "x8", "hot"],
            ["frp", "persist"],
            [],
            true);

        public static readonly BrandProfile Tecno = new(
            "teco", "Tecno",
            OperationTypes.TecnoReadInfo, OperationTypes.TecnoResetFrp, OperationTypes.TecnoWipe,
            ["tecno", "techno"],
            ["mt", "sm", "ums", "sc"],
            ["tecno", "camon", "spark", "pova", "phantom"],
            ["frp", "frp_p"],
            ["userdata", "cache"],
            false);

        public static readonly BrandProfile Spreadtrum = new(
            "spreadtrum", "SPREADTRUM (Unisoc)",
            OperationTypes.SpreadtrumReadInfo, OperationTypes.SpreadtrumResetFrp, null,
            ["spreadtrum", "unisoc", "sprd"],
            ["sp", "ums", "sc", "sprd", "shark"],
            ["sprd", "ums", "sc", "uni"],
            ["frp"],
            [],
            false);

        public static readonly IReadOnlyList<BrandProfile> All = [Xiaomi, Infinix, Tecno, Spreadtrum];

        public static BrandProfile Find(string key)
            => All.First(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Brand service module for Xiaomi, Infinix, Tecno and SPREADTRUM (Unisoc) handsets.
    /// "Read device information" runs through adb and verifies the brand from the properties the
    /// handset really reports. FRP/config maintenance runs the documented fastboot erase sequence
    /// against a device that is actually in fastboot mode; nothing is simulated and every command
    /// is logged with its exit code.
    /// </summary>
    public sealed class BrandModuleExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;
        private readonly IProcessRunner _runner;
        private readonly BrandProfile _profile;

        public BrandModuleExecutor(
            BrandProfile profile,
            IAdbClient adb,
            IToolLocator tools,
            IProcessRunner runner,
            IReadOnlyList<string> supportedOperations)
        {
            _profile = profile;
            _adb = adb;
            _tools = tools;
            _runner = runner;
            SupportedOperations = supportedOperations;
        }

        public IReadOnlyList<string> SupportedOperations { get; }

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            try
            {
                if (string.Equals(request.OperationType, _profile.ReadInfoOperation, StringComparison.OrdinalIgnoreCase))
                {
                    return await ReadInfoAsync(request, ct);
                }

                if (string.Equals(request.OperationType, _profile.FrpOperation, StringComparison.OrdinalIgnoreCase))
                {
                    return await FastbootMaintenanceAsync(request, "FRP reset", _profile.FrpPartitions, factoryFormat: false, ct);
                }

                if (_profile.WipeOperation is not null &&
                    string.Equals(request.OperationType, _profile.WipeOperation, StringComparison.OrdinalIgnoreCase))
                {
                    return _profile.WipeIsFactoryFormat
                        ? await FastbootMaintenanceAsync(request, "factory format", [], factoryFormat: true, ct)
                        : await FastbootMaintenanceAsync(request, "userdata wipe", _profile.WipePartitions, factoryFormat: false, ct);
                }

                return OperationExecutionResult.Failure(request.OperationType,
                    "Unsupported operation.", $"The {_profile.DisplayName} module does not handle '{request.OperationType}'.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    $"{_profile.DisplayName} module failed.", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ read info

        private async Task<OperationExecutionResult> ReadInfoAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required.",
                    "No device serial was supplied. Connect the handset and enable USB debugging, or switch it to fastboot mode.");
            }

            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            IReadOnlyList<Dictionary<string, string>> props = await VendorSupport.PropertiesAsync(_adb, request.Serial, ct);
            if (props.Count == 0)
            {
                errors.Add("The device did not return any properties through adb; check the connection and the USB authorisation prompt.");
                return VendorSupport.Build(request.OperationType, "No data read.", checks, log, warnings, errors);
            }

            string? manufacturer = VendorSupport.Any(props, "ro.product.manufacturer", "ro.product.system.manufacturer", "ro.product.brand", "ro.product.vendor.manufacturer");
            string? brand = VendorSupport.Any(props, "ro.product.brand", "ro.product.system.brand");
            string? platform = VendorSupport.Any(props, "ro.board.platform", "ro.hardware", "ro.soc.model", "ro.chipname", "ro.mediatek.platform");

            bool brandMatch = Matches(manufacturer, _profile.ManufacturerHints) || Matches(brand, _profile.ManufacturerHints);
            checks.Add(new DiagnosticCheckDto("brand-identity", brandMatch ? "pass" : "fail",
                brandMatch
                    ? $"Manufacturer: {manufacturer ?? brand}"
                    : $"The device reports '{manufacturer ?? brand ?? "unknown"}' as manufacturer, which is not a {_profile.DisplayName} handset.",
                null));

            if (!brandMatch)
            {
                errors.Add($"The connected device does not identify itself as {_profile.DisplayName}.");
                log.Add($"{_profile.DisplayName} module stopped: identity check failed.");
                return VendorSupport.Build(request.OperationType, $"Not a {_profile.DisplayName} device.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("board-platform", platform is null ? "skip" : "pass",
                platform is null ? "The board platform was not reported." : $"Board platform: {platform}", null));

            bool platformMatch = Matches(platform, _profile.PlatformHints);
            checks.Add(new DiagnosticCheckDto("chipset-family", platform is null ? "skip" : platformMatch ? "pass" : "warn",
                platformMatch
                    ? "The chipset matches the expected families for this module."
                    : "The chipset does not match the usual families for this module; diagnostics still ran.",
                null));

            (string key, string label)[] fields =
            [
                ("ro.product.model", "Model"),
                ("ro.product.device", "Device"),
                ("ro.build.display.id", "Build"),
                ("ro.build.version.release", "Android version"),
                ("ro.build.version.sdk", "API level"),
                ("ro.build.version.security_patch", "Security patch"),
                ("ro.build.fingerprint", "Fingerprint"),
                ("ro.product.cpu.abi", "CPU ABI"),
                ("ro.boot.hardware", "Boot hardware"),
                ("ro.bootloader", "Bootloader")
            ];

            foreach ((string key, string label) in fields)
            {
                string? value = VendorSupport.Get(props, key);
                checks.Add(new DiagnosticCheckDto(key, value is null ? "skip" : "pass",
                    value is null ? $"{label} was not reported." : $"{label}: {VendorSupport.Truncate(value, 160)}", null));
            }

            string? frp = VendorSupport.Any(props, "ro.frp.pst", "ro.frp");
            checks.Add(new DiagnosticCheckDto("frp-state", frp is null ? "skip" : "pass",
                frp is null ? "ro.frp.pst was not reported by this build." : $"FRP partition state: {frp}", null));

            string? oemUnlock = VendorSupport.Any(props, "ro.oem_unlock_supported", "sys.oem_unlock_allowed");
            checks.Add(new DiagnosticCheckDto("oem-unlock", oemUnlock is null ? "skip" : "pass",
                oemUnlock is null ? "OEM unlock support was not reported." : $"OEM unlock supported: {oemUnlock}", null));

            if (string.Equals(_profile.Key, BrandProfiles.Spreadtrum.Key, StringComparison.OrdinalIgnoreCase))
            {
                await AddSpreadtrumPortsAsync(checks, log, ct);
            }

            warnings.Add($"{_profile.DisplayName} maintenance routines (FRP / partition erase) run in fastboot mode and require a factory/unlocked bootloader.");
            log.Add($"{_profile.DisplayName} module: identity, chipset, build and FRP state read through adb getprop.");

            string? model = VendorSupport.Get(props, "ro.product.model");
            return VendorSupport.Build(request.OperationType,
                $"{_profile.DisplayName} device information read for {model ?? request.Serial}.", checks, log, warnings, errors);
        }

        /// <summary>SPRD diag/preloader ports appear as virtual COM ports with a Unisoc device name.</summary>
        private async Task AddSpreadtrumPortsAsync(List<DiagnosticCheckDto> checks, List<string> log, CancellationToken ct)
        {
            IReadOnlyList<(string Port, string Description)> ports = await SerialPortProbe.ListAsync(_runner, ct);
            if (ports.Count == 0)
            {
                checks.Add(new DiagnosticCheckDto("sprd-com-ports", "skip",
                    "No virtual COM port is present; the handset is in normal ADB mode.", null));
                return;
            }

            string joined = string.Join(", ", ports.Select(p => $"{p.Port} ({p.Description})"));
            checks.Add(new DiagnosticCheckDto("sprd-com-ports", "pass", $"Serial ports: {VendorSupport.Truncate(joined, 220)}", null));

            bool diag = ports.Any(p => ContainsAny(p.Description, "sprd", "unisoc", "diag", "spreadtrum"));
            checks.Add(new DiagnosticCheckDto("sprd-diag-interface", diag ? "pass" : "warn",
                diag
                    ? "A SPRD diag/preloader interface is present."
                    : "COM ports exist but none looks like a SPRD diag interface.",
                null));
            log.Add($"SPREADTRUM port probe: {joined}");
        }

        // ------------------------------------------------------------------ fastboot

        private async Task<OperationExecutionResult> FastbootMaintenanceAsync(
            OperationExecutionRequest request, string routine, string[] partitions, bool factoryFormat, CancellationToken ct)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{routine}' must be confirmed explicitly before it runs: it erases protected partitions. " +
                           "Confirm the warning in the client and retry.");
                return VendorSupport.Build(request.OperationType, "Confirmation required.", checks, log, warnings, errors);
            }

            string? fastboot = _tools.Find(ToolLocator.Fastboot);
            if (fastboot is null)
            {
                errors.Add("fastboot was not found. Install the bundled platform-tools next to the application.");
                return VendorSupport.Build(request.OperationType, "fastboot is unavailable.", checks, log, warnings, errors);
            }

            ProcessResult list = await _runner.RunAsync(new ProcessRequest(fastboot, ["devices"], TimeoutSeconds: 30), ct);
            string connected = list.StandardOutput.Trim();
            checks.Add(new DiagnosticCheckDto("fastboot-device", string.IsNullOrWhiteSpace(connected) ? "fail" : "pass",
                string.IsNullOrWhiteSpace(connected)
                    ? "No fastboot device answered. Boot the handset into fastboot/bootloader mode and retry."
                    : $"fastboot device: {VendorSupport.Truncate(connected.Replace(Environment.NewLine, " "), 180)}",
                null));

            if (string.IsNullOrWhiteSpace(connected))
            {
                errors.Add($"{_profile.DisplayName} {routine} needs the handset in fastboot mode.");
                return VendorSupport.Build(request.OperationType, "No fastboot device.", checks, log, warnings, errors);
            }

            ProcessResult product = await _runner.RunAsync(new ProcessRequest(fastboot, ["getvar", "product"], TimeoutSeconds: 30), ct);
            string productLine = FirstLine(product.StandardOutput + " " + product.StandardError);
            checks.Add(new DiagnosticCheckDto("fastboot-product", "pass",
                string.IsNullOrWhiteSpace(productLine) ? "The bootloader did not report a product name." : $"Product: {VendorSupport.Truncate(productLine, 120)}", null));

            if (!Matches(product.StandardOutput + product.StandardError, _profile.ProductHints))
            {
                warnings.Add($"The bootloader product does not look like a {_profile.DisplayName} handset; verify the target before continuing.");
            }

            bool allSucceeded = true;

            if (factoryFormat)
            {
                ProcessResult format = await _runner.RunAsync(new ProcessRequest(fastboot, ["-w"], TimeoutSeconds: 420), ct);
                allSucceeded &= format.Succeeded;
                log.Add($"fastboot -w -> exit {format.ExitCode?.ToString() ?? "?"}: {VendorSupport.Truncate(FirstLine(format.StandardOutput + " " + format.StandardError), 200)}");
                if (!format.Succeeded)
                {
                    errors.Add($"Factory format failed: {FirstLine(format.StandardError)}");
                }
            }

            foreach (string partition in partitions)
            {
                ProcessResult erase = await _runner.RunAsync(new ProcessRequest(fastboot, ["erase", partition], TimeoutSeconds: 180), ct);
                allSucceeded &= erase.Succeeded;
                string output = FirstLine(erase.StandardOutput + " " + erase.StandardError);
                log.Add($"fastboot erase {partition} -> exit {erase.ExitCode?.ToString() ?? "?"}: {VendorSupport.Truncate(output, 200)}");
                checks.Add(new DiagnosticCheckDto($"erase-{partition}", erase.Succeeded ? "pass" : "fail",
                    erase.Succeeded ? $"Partition '{partition}' erased." : $"Partition '{partition}' failed: {VendorSupport.Truncate(output, 200)}", null));

                if (!erase.Succeeded)
                {
                    errors.Add($"fastboot erase {partition} failed: {VendorSupport.Truncate(output, 200)}");
                }
            }

            warnings.Add("Reboot the handset after a FRP/partition routine and confirm that setup completes normally.");
            log.Add($"{_profile.DisplayName} {routine}: {partitions.Length} partition command(s) executed through fastboot.");

            string summary = allSucceeded
                ? $"{_profile.DisplayName} {routine} completed."
                : $"{_profile.DisplayName} {routine} finished with errors.";

            return VendorSupport.Build(request.OperationType, summary, checks, log, warnings, errors);
        }

        private static string FirstLine(string value)
        {
            string first = value.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return first;
        }

        private static bool Matches(string? value, IEnumerable<string> hints)
            => value is not null && ContainsAny(value, hints.ToArray());

        private static bool ContainsAny(string? value, params string[] hints)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (string hint in hints)
            {
                if (value.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Reads the virtual COM ports Windows exposes, used for Unisoc/SPRD diag detection.</summary>
    internal static class SerialPortProbe
    {
        private const string Script =
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "try { Get-CimInstance -ClassName Win32_SerialPort | Select-Object DeviceID,Description | ConvertTo-Json -Compress } catch { '' }";

        public static async Task<IReadOnlyList<(string Port, string Description)>> ListAsync(IProcessRunner runner, CancellationToken ct)
        {
            try
            {
                ProcessResult result = await runner.RunAsync(new ProcessRequest(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", Script],
                    TimeoutSeconds: 25), ct);

                if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    return Array.Empty<(string, string)>();
                }

                return Parse(result.StandardOutput);
            }
            catch (Exception)
            {
                // Port enumeration is informational: it must never fail the module.
                return Array.Empty<(string, string)>();
            }
        }

        private static IReadOnlyList<(string Port, string Description)> Parse(string json)
        {
            List<(string, string)> ports = new();
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    ports.Add(Read(root));
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in root.EnumerateArray())
                    {
                        ports.Add(Read(item));
                    }
                }
            }
            catch (JsonException)
            {
                // An unexpected shape simply means "no ports detected".
            }

            return ports;
        }

        private static (string, string) Read(JsonElement item)
        {
            string port = item.TryGetProperty("DeviceID", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty;
            string description = item.TryGetProperty("Description", out JsonElement descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            return (port, description);
        }
    }
}
