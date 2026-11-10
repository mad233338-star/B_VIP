using System.IO;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// Qualcomm FRP through <c>fh_loader.exe</c>: the handset is in emergency download mode (9008),
    /// the Firehose programmer is started on its COM port and the protected partitions are erased
    /// directly, which is the only route on a phone whose bootloader is still locked.
    ///
    /// The programmer is found next to the tool when the operator has not chosen one, so the command
    /// stays the documented <c>fh_loader --port=\\.\COMx --erase=frp --erase=config --reset</c>.
    /// </summary>
    public sealed class FhLoaderFrpExecutor : IOperationExecutor
    {
        private readonly IProcessRunner _runner;
        private readonly IVendorToolchain _toolchain;

        public FhLoaderFrpExecutor(IProcessRunner runner, IVendorToolchain? toolchain = null)
        {
            _runner = runner;
            _toolchain = toolchain ?? new VendorToolchain();
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.QualcommFrpLoader];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            VendorCliSession session = new(_runner, request);

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                session.Errors.Add("Erasing FRP through EDL must be confirmed explicitly before it runs. " +
                                   "Confirm the warning in the client and retry.");
                session.Checks.Add(new DiagnosticCheckDto("confirm", "fail", "The routine was not confirmed.", null));
                return VendorSupport.Build(request.OperationType, "Confirmation required.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            VendorToolResolution tool = await Task.Run(() => _toolchain.ResolveFhLoader(), ct);
            if (!tool.Found)
            {
                session.Errors.Add(tool.Detail);
                session.Checks.Add(new DiagnosticCheckDto("fh_loader", "fail", tool.Detail, null));
                return VendorSupport.Build(request.OperationType, "fh_loader is not available.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("fh_loader", true, tool.Detail);

            string? port = NormalisePort(request.GetParameter("port")) ?? NormalisePort(request.Serial);
            if (port is null)
            {
                const string detail = "No 9008 COM port was selected. Put the handset in EDL mode, refresh the interface list " +
                                      "and select its COM port before running the routine.";
                session.Errors.Add(detail);
                session.Checks.Add(new DiagnosticCheckDto("edl-port", "fail", detail, null));
                return VendorSupport.Build(request.OperationType, "No EDL port selected.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("edl-port", true, $"EDL port: {port}");

            string? programmer = FirstExisting(request.GetParameter("firehosePath")) ?? _toolchain.FindFirehoseProgrammer();
            List<string> arguments =
            [
                $"--port=\\\\.\\{port}",
                "--erase=frp",
                "--erase=config",
                "--reset",
                "--noprompt"
            ];

            if (programmer is not null)
            {
                string folder = Path.GetDirectoryName(programmer) ?? Path.GetDirectoryName(tool.Executable) ?? ".";
                arguments.Add($"--search_path={folder}");
                session.Check("programmer", true, $"Programmer: {Path.GetFileName(programmer)}");
                session.Note($"fh_loader will start {Path.GetFileName(programmer)} from {folder}.");
            }
            else
            {
                session.Warn("No Firehose programmer was found in the Qualcomm loader bundle. fh_loader will use the " +
                             "first prog_firehose_*.mbn it finds beside itself, which may not match this SoC.");
                arguments.Add($"--search_path={Path.GetDirectoryName(tool.Executable) ?? "."}");
            }

            string partitions = request.GetParameter("partitions") is { Length: > 0 } extra
                ? NormalisePartitions(extra)
                : "frp,config";

            // The documented sequence is exactly frp + config; a caller can narrow it, never widen it.
            arguments.RemoveAll(argument => argument.StartsWith("--erase=", StringComparison.OrdinalIgnoreCase));
            foreach (string partition in partitions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                arguments.Insert(0, $"--erase={partition}");
            }

            int timeout = ParseTimeout(request.GetParameter("timeoutSeconds"), 600);

            session.Stage($"Erasing '{partitions}' through EDL on {port}.");
            ProcessResult? result = await session.RunAsync("edl-erase", tool.Executable, arguments,
                Path.GetDirectoryName(tool.Executable), timeout, ct);

            if (result is null || !result.Succeeded)
            {
                return VendorSupport.Build(request.OperationType,
                    session.Cancelled ? "Cancelled." : "The EDL erase did not complete.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Warn("The handset resets into Android. Complete the setup once and confirm the Google account step is gone.");

            return VendorSupport.Build(request.OperationType,
                $"FRP erased through EDL ({partitions}) and the handset was reset.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        /// <summary>COM3, \\.\COM3 and "Qualcomm HS-USB QDLoader 9008 (COM3)" all resolve to COM3.</summary>
        internal static string? NormalisePort(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string text = value.Trim();

            // "Qualcomm" itself contains "com", so only the occurrence followed by digits is a port.
            for (int marker = text.IndexOf("COM", StringComparison.OrdinalIgnoreCase); marker >= 0;
                 marker = text.IndexOf("COM", marker + 3, StringComparison.OrdinalIgnoreCase))
            {
                int end = marker + 3;
                while (end < text.Length && char.IsDigit(text[end]))
                {
                    end++;
                }

                if (end > marker + 3)
                {
                    return text[marker..end].ToUpperInvariant();
                }
            }

            return null;
        }

        internal static string NormalisePartitions(string requested)
        {
            string[] allowed = ["frp", "config", "persistent", "persist", "frp_p"];
            string[] asked = requested
                .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.ToLowerInvariant())
                .Where(allowed.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return asked.Length == 0 ? "frp,config" : string.Join(',', asked);
        }

        internal static int ParseTimeout(string? value, int fallback)
            => int.TryParse(value, out int seconds) && seconds is >= 30 and <= 3600 ? seconds : fallback;

        private static string? FirstExisting(string? path)
            => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }
}
