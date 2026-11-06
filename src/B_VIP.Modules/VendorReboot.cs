using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>
    /// Reboots a handset into the mode the technician asked for, whichever transport is available.
    ///
    /// ADB is tried first (it is present whenever the device is booted and authorized); if that
    /// fails the same target is attempted over fastboot, which is the only route to EDL on most
    /// Qualcomm and MediaTek handsets. Both attempts are logged with their real exit status, and a
    /// target that neither transport offers is reported instead of being pretended.
    /// </summary>
    public sealed class VendorRebootExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;

        public VendorRebootExecutor(IAdbClient adb)
        {
            _adb = adb;
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.VendorReboot];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required.",
                    "No device serial was supplied. Connect the handset and enable USB debugging, or switch it to fastboot mode.");
            }

            string target = Normalize(request.GetParameter("target"));

            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Process($"Rebooting {request.Serial} to {target}.");

            (bool Attempted, bool Succeeded, string Detail) adb = await TryAdbAsync(request, target, log, ct);
            if (adb.Succeeded)
            {
                checks.Add(new DiagnosticCheckDto("reboot-adb", "pass", adb.Detail, null));
                request.Success(adb.Detail);
                warnings.Add("The handset reboots immediately; USB debugging is dropped until it comes back up.");
                return VendorSupport.Build(request.OperationType, $"Reboot to {target} requested over adb.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("reboot-adb", "warn", adb.Detail, null));
            log.Add($"adb route failed: {adb.Detail}");

            (bool Attempted, bool Succeeded, string Detail) fastboot = await TryFastbootAsync(request, target, log, ct);
            if (fastboot.Succeeded)
            {
                checks.Add(new DiagnosticCheckDto("reboot-fastboot", "pass", fastboot.Detail, null));
                request.Success(fastboot.Detail);
                return VendorSupport.Build(request.OperationType, $"Reboot to {target} requested over fastboot.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("reboot-fastboot", "warn", fastboot.Detail, null));

            errors.Add(target switch
            {
                "edl" => "Neither adb nor fastboot accepted the EDL request. EDL needs an unlocked bootloader and a handset " +
                         "whose bootloader implements 'oem edl'.",
                "download" => "Neither adb nor fastboot accepted the Download-mode request. This mode is Samsung-specific " +
                              "and needs an authorized adb connection.",
                _ => $"The handset did not accept a reboot to {target} over adb or fastboot."
            });

            return VendorSupport.Build(request.OperationType, $"Reboot to {target} failed.", checks, log, warnings, errors);
        }

        private async Task<(bool Attempted, bool Succeeded, string Detail)> TryAdbAsync(
            OperationExecutionRequest request, string target, List<string> log, CancellationToken ct)
        {
            string[] arguments = target is "system"
                ? ["reboot"]
                : target is "bootloader"
                    ? ["reboot", "bootloader"]
                    : ["reboot", target];

            List<string> command = ["-s", request.Serial!, .. arguments];
            ProcessResult result = await _adb.AdbAsync(command, 45, ct);
            log.Add($"$ adb {string.Join(' ', command)}");
            log.AddRange(AdbOperationExecutor.AppendOutput(result));

            string detail = result.Succeeded
                ? $"adb {string.Join(' ', arguments)} accepted."
                : result.FailureReason ?? VendorSupport.FirstLine(result.StandardError);

            return (true, result.Succeeded, detail);
        }

        private async Task<(bool Attempted, bool Succeeded, string Detail)> TryFastbootAsync(
            OperationExecutionRequest request, string target, List<string> log, CancellationToken ct)
        {
            string[] arguments = target switch
            {
                "system" => ["reboot"],
                "recovery" => ["reboot", "recovery"],
                "bootloader" => ["reboot", "bootloader"],
                "edl" => ["oem", "edl"],
                "download" => ["oem", "reboot-download"],
                _ => ["reboot"]
            };

            List<string> command = ["-s", request.Serial!, .. arguments];
            ProcessResult result = await _adb.FastbootAsync(command, 60, ct);
            log.Add($"$ fastboot {string.Join(' ', command)}");
            log.AddRange(AdbOperationExecutor.AppendOutput(result));

            string detail = result.Succeeded
                ? $"fastboot {string.Join(' ', arguments)} accepted."
                : result.FailureReason ?? VendorSupport.FirstLine(result.StandardError + " " + result.StandardOutput);

            return (true, result.Succeeded, detail);
        }

        private static string Normalize(string? target) => target?.Trim().ToLowerInvariant() switch
        {
            "fastboot" or "bootloader" => "bootloader",
            "recovery" => "recovery",
            "edl" => "edl",
            "download" => "download",
            _ => "system"
        };
    }
}
