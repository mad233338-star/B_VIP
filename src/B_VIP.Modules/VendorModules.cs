using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Modules;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>Shared helpers for the vendor modules: property access and availability mapping.</summary>
    internal static class VendorSupport
    {
        public static async Task<IReadOnlyList<Dictionary<string, string>>> PropertiesAsync(IAdbClient adb, string serial, CancellationToken ct)
            => await adb.GetPropertiesAsync(serial, ct);

        public static string? Get(IReadOnlyList<Dictionary<string, string>> props, string key)
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

        public static string? Any(IReadOnlyList<Dictionary<string, string>> props, params string[] keys)
        {
            foreach (string key in keys)
            {
                string? value = Get(props, key);
                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        public static string Truncate(string value, int max)
            => value.Length <= max ? value : value[..max];

        /// <summary>The first non-empty line of a tool's output, for one-line log messages.</summary>
        public static string FirstLine(string value)
            => value.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

        public static OperationExecutionResult Build(string operationType, string summary,
            List<DiagnosticCheckDto> checks, List<string> log, List<string> warnings, List<string> errors)
        {
            bool succeeded = errors.Count == 0;
            OperationOutcome outcome = succeeded
                ? (warnings.Count > 0 ? OperationOutcome.RequiresAttention : OperationOutcome.Succeeded)
                : OperationOutcome.Failed;

            return new OperationExecutionResult(operationType, succeeded, outcome, succeeded ? summary : errors[0],
                checks, log, warnings, errors);
        }
    }

    /// <summary>
    /// Samsung diagnostics: verifies the handset really is a Samsung device, reads the firmware and
    /// CSC identifiers the panel shows, and reports the Knox warranty flag exactly as the device
    /// reports it. Download-mode flashing through Odin is proprietary and is not simulated here.
    /// </summary>
    public sealed class SamsungModuleExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;
        private readonly Samsung.SamsungExecutionService _samsung;

        public SamsungModuleExecutor(IAdbClient adb, Samsung.SamsungExecutionService samsung)
        {
            _adb = adb;
            _samsung = samsung;
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.SamsungDiagnostic,
            OperationTypes.SamsungReadInfoMtp,
            OperationTypes.SamsungRebootDownload,
            OperationTypes.SamsungRebootSystem,
            OperationTypes.SamsungOpenBrowser,
            OperationTypes.SamsungFrpTestMode
        ];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            switch (request.OperationType)
            {
                case OperationTypes.SamsungReadInfoMtp:
                    return await _samsung.ReadInfoAsync(request, ct);

                case OperationTypes.SamsungRebootDownload:
                    return await _samsung.RebootAsync(request, "download", ct);

                case OperationTypes.SamsungRebootSystem:
                    return await _samsung.RebootAsync(request, "system", ct);

                case OperationTypes.SamsungOpenBrowser:
                    return await OpenBrowserAsync(request, ct);

                case OperationTypes.SamsungFrpTestMode:
                    return await _samsung.ResetFrpTestModeAsync(request, ct);
            }

            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected Samsung device is required.", "No device serial was supplied.");
            }

            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            IReadOnlyList<Dictionary<string, string>> props = await VendorSupport.PropertiesAsync(_adb, request.Serial, ct);
            if (props.Count == 0)
            {
                errors.Add("The device did not return any properties through adb; check the connection and authorisation.");
                return VendorSupport.Build(request.OperationType, "No data read.", checks, log, warnings, errors);
            }

            string? manufacturer = VendorSupport.Any(props, "ro.product.manufacturer", "ro.product.system.manufacturer");
            bool isSamsung = manufacturer?.Contains("samsung", StringComparison.OrdinalIgnoreCase) == true;
            checks.Add(new DiagnosticCheckDto("samsung-identity", isSamsung ? "pass" : "fail",
                isSamsung ? $"Manufacturer: {manufacturer}" : $"This device reports manufacturer '{manufacturer ?? "unknown"}' and is not a Samsung handset.", null));
            if (!isSamsung)
            {
                errors.Add("The connected device is not a Samsung handset.");
                log.Add("Samsung module stopped: identity check failed.");
                return VendorSupport.Build(request.OperationType, "Not a Samsung device.", checks, log, warnings, errors);
            }

            string? model = VendorSupport.Any(props, "ro.product.model", "ro.product.system.model");
            string? csc = VendorSupport.Any(props, "ro.csc.product_code", "ro.product.salescode", "ro.csc.countryiso");
            string? firmware = VendorSupport.Any(props, "ro.build.PDA", "ro.build.version.incremental", "ro.build.display.id");
            checks.Add(new DiagnosticCheckDto("samsung-model", model is null ? "skip" : "pass", model is null ? "Model was not reported." : $"Model: {model}", null));
            checks.Add(new DiagnosticCheckDto("samsung-csc", csc is null ? "skip" : "pass", csc is null ? "CSC/sales code was not reported." : $"CSC: {csc}", null));
            checks.Add(new DiagnosticCheckDto("samsung-firmware", firmware is null ? "skip" : "pass", firmware is null ? "Firmware build was not reported." : $"Firmware: {firmware}", null));

            string? warrantyBit = VendorSupport.Any(props, "ro.boot.warranty_bit", "ro.warranty_bit", "ro.boot.knox_status");
            if (warrantyBit is not null)
            {
                bool tripped = warrantyBit is not "0";
                checks.Add(new DiagnosticCheckDto("knox-warranty-flag", tripped ? "warn" : "pass",
                    $"Warranty/Knox flag reported by the device: {warrantyBit}", null));
                if (tripped)
                {
                    warnings.Add("The device reports a tripped warranty/Knox flag.");
                }
            }
            else
            {
                checks.Add(new DiagnosticCheckDto("knox-warranty-flag", "skip", "The device did not expose a warranty flag property.", null));
            }

            string? bootReason = VendorSupport.Any(props, "ro.boot.reason", "sys.boot.reason");
            checks.Add(new DiagnosticCheckDto("boot-reason", bootReason is null ? "skip" : "pass",
                bootReason is null ? "Boot reason was not reported." : $"Boot reason: {bootReason}", null));

            log.Add("Samsung module: identity, firmware and CSC checks executed through adb getprop.");
            warnings.Add("Odin/Download-mode firmware writing is a proprietary Samsung flow and is not performed by this module.");

            return VendorSupport.Build(request.OperationType,
                $"Samsung diagnostics completed for {model ?? request.Serial}.", checks, log, warnings, errors);
        }

        private async Task<OperationExecutionResult> OpenBrowserAsync(OperationExecutionRequest request, CancellationToken ct)
        {
            string key = request.GetParameter("target") ?? "youtube";
            Samsung.BrowserTarget target = key.ToLowerInvariant() switch
            {
                "chrome" => Samsung.BrowserTarget.Chrome,
                "samsung-internet" => Samsung.BrowserTarget.SamsungInternet,
                "custom" => Samsung.BrowserTarget.Custom(request.GetParameter("url") ?? string.Empty),
                _ => Samsung.BrowserTarget.YouTube
            };

            request.Info($"Opening {target.Label} on the handset screen.");
            Samsung.BrowserOpenResult result = await _samsung.OpenBrowserAsync(request.Serial, target, ct);

            List<DiagnosticCheckDto> checks =
            [
                new DiagnosticCheckDto("browser", result.Succeeded ? "pass" : "fail", result.Detail, null)
            ];
            List<string> log = result.Command is null ? new List<string>() : new List<string> { $"$ {result.Command}" };
            List<string> warnings = new();
            List<string> errors = new();

            if (!result.Succeeded)
            {
                errors.Add(result.Detail);
            }
            else
            {
                warnings.Add("The page opens only when the handset has an active connection and the target browser is installed.");
            }

            return VendorSupport.Build(request.OperationType, result.Detail, checks, log, warnings, errors);
        }
    }

    /// <summary>
    /// MediaTek diagnostics: identifies the MT platform from the device properties and reports what
    /// a real service workflow would need next. Preloader/SP Flash Tool programming requires the
    /// vendor tool and a test point, so the module reports that requirement instead of pretending.
    /// </summary>
    public sealed class MediaTekModuleExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;

        public MediaTekModuleExecutor(IAdbClient adb)
        {
            _adb = adb;
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.MediaTekDiagnostic];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required.", "No device serial was supplied.");
            }

            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            IReadOnlyList<Dictionary<string, string>> props = await VendorSupport.PropertiesAsync(_adb, request.Serial, ct);
            if (props.Count == 0)
            {
                errors.Add("The device did not return any properties through adb; check the connection and authorisation.");
                return VendorSupport.Build(request.OperationType, "No data read.", checks, log, warnings, errors);
            }

            string? platform = VendorSupport.Any(props, "ro.board.platform", "ro.hardware", "ro.chipname", "ro.mediatek.platform");
            bool isMediaTek = platform is not null &&
                (platform.StartsWith("mt", StringComparison.OrdinalIgnoreCase) ||
                 platform.Contains("mediatek", StringComparison.OrdinalIgnoreCase));

            checks.Add(new DiagnosticCheckDto("mediatek-platform", platform is null ? "skip" : isMediaTek ? "pass" : "fail",
                platform is null ? "Board platform was not reported." : $"Board platform: {platform}", null));

            if (!isMediaTek)
            {
                errors.Add($"The device reports platform '{platform ?? "unknown"}', so it does not appear to be MediaTek based.");
                return VendorSupport.Build(request.OperationType, "Not a MediaTek device.", checks, log, warnings, errors);
            }

            string? soc = VendorSupport.Any(props, "ro.soc.model", "ro.mediatek.platform");
            string? hardware = VendorSupport.Any(props, "ro.hardware");
            checks.Add(new DiagnosticCheckDto("mediatek-soc", soc is null ? "skip" : "pass",
                soc is null ? "SoC model was not reported." : $"SoC: {soc}", null));
            checks.Add(new DiagnosticCheckDto("mediatek-hardware", hardware is null ? "skip" : "pass",
                hardware is null ? "Hardware name was not reported." : $"Hardware: {hardware}", null));

            string? bootloader = VendorSupport.Any(props, "ro.boot.verifiedbootstate", "ro.boot.vbmeta.device_state");
            bool unlocked = string.Equals(Normalize(bootloader), "unlocked", StringComparison.OrdinalIgnoreCase);
            checks.Add(new DiagnosticCheckDto("bootloader-state", bootloader is null ? "skip" : unlocked ? "warn" : "pass",
                bootloader is null ? "Bootloader state was not reported." : $"Bootloader state: {bootloader}", null));

            warnings.Add("Preloader/DA flashing through SP Flash Tool needs the vendor tool, a USB driver and a test point; it is not performed from this module.");
            log.Add("MediaTek module: platform, SoC and bootloader state read through adb getprop.");

            return VendorSupport.Build(request.OperationType,
                $"MediaTek diagnostics completed for {soc ?? platform ?? request.Serial}.", checks, log, warnings, errors);
        }

        private static string Normalize(string? value) => value ?? string.Empty;
    }

    /// <summary>
    /// Qualcomm diagnostics: identifies the MSM/SDM platform and reports the EDL (9008) requirement,
    /// which depends on the Qualcomm QDLoader driver being installed on this computer.
    /// </summary>
    public sealed class QualcommModuleExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;

        public QualcommModuleExecutor(IAdbClient adb, IToolLocator tools)
        {
            _adb = adb;
            _tools = tools;
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.QualcommDiagnostic];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required.", "No device serial was supplied.");
            }

            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            IReadOnlyList<Dictionary<string, string>> props = await VendorSupport.PropertiesAsync(_adb, request.Serial, ct);
            if (props.Count == 0)
            {
                errors.Add("The device did not return any properties through adb; check the connection and authorisation.");
                return VendorSupport.Build(request.OperationType, "No data read.", checks, log, warnings, errors);
            }

            string? platform = VendorSupport.Any(props, "ro.board.platform", "ro.hardware", "ro.soc.model");
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
                errors.Add($"The device reports platform '{platform ?? "unknown"}', so it does not appear to be Qualcomm based.");
                return VendorSupport.Build(request.OperationType, "Not a Qualcomm device.", checks, log, warnings, errors);
            }

            string? soc = VendorSupport.Any(props, "ro.soc.model", "ro.board.platform");
            string? bootloader = VendorSupport.Any(props, "ro.boot.vbmeta.device_state", "ro.boot.verifiedbootstate");
            checks.Add(new DiagnosticCheckDto("qualcomm-soc", soc is null ? "skip" : "pass", soc is null ? "SoC model was not reported." : $"SoC: {soc}", null));
            checks.Add(new DiagnosticCheckDto("bootloader-state", bootloader is null ? "skip" : "pass",
                bootloader is null ? "Bootloader state was not reported." : $"Bootloader state: {bootloader}", null));

            string? fastbootPath = _tools.Find(ToolLocator.Fastboot);
            checks.Add(new DiagnosticCheckDto("fastboot-availability", fastbootPath is null ? "fail" : "pass",
                fastbootPath ?? "fastboot is not installed; partition operations are unavailable.", null));
            if (fastbootPath is null)
            {
                warnings.Add("Install Android platform-tools to run partition operations in fastboot mode.");
            }

            warnings.Add("EDL (9008) programming requires the Qualcomm QDLoader driver and a device in emergency download mode; that flow is not simulated here.");
            log.Add("Qualcomm module: platform, SoC and bootloader state read through adb getprop.");

            return VendorSupport.Build(request.OperationType,
                $"Qualcomm diagnostics completed for {soc ?? platform ?? request.Serial}.", checks, log, warnings, errors);
        }
    }
}
