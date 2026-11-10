using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// Clears the Android setup wizard over an authorized adb connection: the secure
    /// <c>user_setup_complete</c> flag is written, the launcher is asked to come to the front so the
    /// wizard loses its place, and the handset is rebooted. Nothing is erased and no account data is
    /// read; the commands only tell Android that setup has already been completed.
    /// </summary>
    public sealed class SetupWizardBypassExecutor : IOperationExecutor
    {
        private const string SetSetupComplete =
            "content insert --uri content://settings/secure --bind name:s:user_setup_complete --bind value:s:1";

        private const string ShowLauncher =
            "am broadcast -a android.intent.action.MAIN -c android.intent.category.HOME";

        private readonly IAdbClient _adb;

        public SetupWizardBypassExecutor(IAdbClient adb) => _adb = adb;

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.AdbSetupWizardBypass];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (string.IsNullOrWhiteSpace(request.Serial))
            {
                errors.Add("A connected, authorized adb device is required. Enable USB debugging and allow this computer on the phone.");
                return VendorSupport.Build(request.OperationType, "No device serial.", checks, log, warnings, errors);
            }

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("The setup-wizard bypass must be confirmed explicitly before it runs: it changes the phone's setup state. " +
                           "Confirm the warning in the client and retry.");
                return VendorSupport.Build(request.OperationType, "Confirmation required.", checks, log, warnings, errors);
            }

            request.Process($"Clearing the setup-wizard flags on {request.Serial}.");

            // The insert is the only command that matters; the broadcast only makes the launcher take
            // focus. A build that refuses the broadcast is a warning, not a failure.
            (string Key, string Command, bool Fatal)[] steps =
            [
                ("setup-complete", SetSetupComplete, true),
                ("launcher", ShowLauncher, false)
            ];

            foreach ((string key, string command, bool fatal) in steps)
            {
                if (ct.IsCancellationRequested)
                {
                    return new OperationExecutionResult(request.OperationType, false, OperationOutcome.Cancelled,
                        "Cancelled.", checks, log, warnings, new[] { "Cancelled by the user." });
                }

                ProcessResult result = await _adb.ShellAsync(request.Serial, command, 60, ct);
                string output = VendorSupport.FirstLine(result.StandardOutput + " " + result.StandardError);

                log.Add($"$ adb -s {request.Serial} shell {command}");
                if (output.Length > 0)
                {
                    log.Add("  " + VendorSupport.Truncate(output, 240));
                }

                log.Add($"  exit {result.ExitCode?.ToString() ?? "?"}");

                if (result.Succeeded)
                {
                    checks.Add(new DiagnosticCheckDto(key, "pass", $"Done (exit {result.ExitCode}).", result.DurationMs));
                    request.Success($"{key}: done.");
                    continue;
                }

                string reason = string.IsNullOrWhiteSpace(result.FailureReason)
                    ? VendorSupport.Truncate(output.Length == 0 ? "refused by the build" : output, 200)
                    : result.FailureReason!;

                checks.Add(new DiagnosticCheckDto(key, fatal ? "fail" : "warn", reason, result.DurationMs));

                if (fatal)
                {
                    errors.Add($"'{command}' failed: {reason}");
                }
                else
                {
                    warnings.Add($"The launcher broadcast was refused ({reason}); the setup flag was still cleared.");
                }
            }

            if (errors.Count > 0)
            {
                return VendorSupport.Build(request.OperationType, "The setup wizard could not be cleared.",
                    checks, log, warnings, errors);
            }

            // A reboot makes the launcher read the new flag; without it the wizard stays on screen.
            request.Process("Rebooting the handset so the new setup state takes effect.");
            ProcessResult reboot = await _adb.AdbSerialAsync(request.Serial, ["reboot"], 30, ct);
            checks.Add(new DiagnosticCheckDto("reboot", reboot.Succeeded ? "pass" : "warn",
                reboot.Succeeded ? "Reboot requested." : VendorSupport.Truncate(reboot.FailureReason ?? "The reboot was refused.", 200),
                reboot.DurationMs));

            if (!reboot.Succeeded)
            {
                warnings.Add("The setup flag was cleared but the reboot was refused. Reboot the handset by hand.");
            }

            warnings.Add("This completes setup on the current system. A factory reset restores the wizard until the FRP partition is cleared.");

            return VendorSupport.Build(request.OperationType,
                "Setup wizard cleared over ADB; the handset was rebooted into Android.",
                checks, log, warnings, errors);
        }
    }
}
