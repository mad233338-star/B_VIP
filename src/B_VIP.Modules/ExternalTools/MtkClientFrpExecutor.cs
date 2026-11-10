using System.IO;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// MediaTek FRP through mtkclient: the handset is held in preloader/BROM mode (Vol+ and Vol- while
    /// the cable is connected) and the partitions are erased through the boot ROM, then the handset is
    /// reset. This is the path that works on a locked bootloader, where fastboot refuses the erase.
    ///
    /// Samsung handsets use the same tool with Samsung's own download agent
    /// (<c>--da-file Loaders/MediaTek/samsung_da.bin</c>), which is why both the MediaTek screen and the
    /// Samsung preloader routine share this executor.
    /// </summary>
    public sealed class MtkClientFrpExecutor : IOperationExecutor
    {
        private readonly IProcessRunner _runner;
        private readonly IVendorToolchain _toolchain;

        public MtkClientFrpExecutor(IProcessRunner runner, IVendorToolchain? toolchain = null)
        {
            _runner = runner;
            _toolchain = toolchain ?? new VendorToolchain();
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.MtkFrpClient,
            OperationTypes.SamsungFrpPreloader
        ];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            VendorCliSession session = new(_runner, request);

            if (!IsConfirmed(request))
            {
                session.Errors.Add("Erasing FRP through the boot ROM must be confirmed explicitly before it runs. " +
                                   "Confirm the warning in the client and retry.");
                session.Checks.Add(new DiagnosticCheckDto("confirm", "fail",
                    "The routine was not confirmed.", null));
                return VendorSupport.Build(request.OperationType, "Confirmation required.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            bool samsung = string.Equals(request.OperationType, OperationTypes.SamsungFrpPreloader, StringComparison.OrdinalIgnoreCase);

            // Scanning the loader bundle touches the disk: keep it off the UI thread.
            VendorToolResolution tool = await Task.Run(() => _toolchain.ResolveMtkClient(), ct);
            if (!tool.Found || tool.EntryScript is null)
            {
                session.Errors.Add(tool.Detail);
                session.Checks.Add(new DiagnosticCheckDto("mtkclient", "fail", tool.Detail, null));
                return VendorSupport.Build(request.OperationType, "mtkclient is not available.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("mtkclient", true, tool.Detail);

            string workingDirectory = tool.WorkingDirectory ?? Directory.GetCurrentDirectory();
            string partitions = NormalisePartitions(request.GetParameter("partitions"), samsung);
            int timeout = ParseTimeout(request.GetParameter("timeoutSeconds"), samsung ? 420 : 300);

            List<string> eraseArguments = [tool.EntryScript, "e", partitions];

            string? daFile = request.GetParameter("daFile");
            if (samsung)
            {
                // The documented Samsung command pins its own download agent; fall back to the one the
                // bundle ships, and say so when neither exists rather than erasing with the wrong DA.
                daFile = FirstExisting(daFile) ?? _toolchain.FindSamsungDa();
                if (daFile is null)
                {
                    session.Warn("No Samsung download agent was found (Loaders/MediaTek/samsung_da.bin). " +
                                 "mtkclient will use its built-in DA, which fails on some Samsung boot ROMs. " +
                                 "Place the retail DA in the loader folder and retry if the handset rejects the handshake.");
                }
                else
                {
                    eraseArguments.AddRange(["--da-file", daFile]);
                    session.Check("samsung-da", true, $"Download agent: {Path.GetFileName(daFile)}");
                }
            }
            else
            {
                if (FirstExisting(daFile) is { } explicitDa)
                {
                    eraseArguments.AddRange(["--da-file", explicitDa]);
                }
            }

            if (FirstExisting(request.GetParameter("preloader")) is { } preloader)
            {
                eraseArguments.AddRange(["--preloader", preloader]);
            }

            if (NormalisePort(request.GetParameter("port")) is { } port)
            {
                eraseArguments.AddRange(["--port", port]);
            }

            session.Stage(samsung
                ? "Power the handset off, then hold Vol+ and Vol- while connecting the cable to enter preloader mode."
                : "Power the handset off, then hold Vol+ (and Vol-) while connecting the cable to enter BROM/preloader mode.");
            session.Stage($"Erasing '{partitions}' through the boot ROM.");

            ProcessResult? erase = await session.RunAsync("erase-frp", tool.Executable, eraseArguments,
                workingDirectory, timeout, ct);

            if (erase is null || !erase.Succeeded || session.Cancelled)
            {
                if (!session.Cancelled && samsung)
                {
                    session.Warn("The preloader routine did not finish. The alternate 'Reset FRP (*#0*# / ADB)' " +
                                 "button drives the Samsung modem and clears the setup flags over an authorized adb connection.");
                }

                return VendorSupport.Build(request.OperationType,
                    session.Cancelled ? "Cancelled." : "The boot-ROM erase did not complete.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            // Reboot last: the phone leaves BROM mode on the reset, so it must not be skipped.
            List<string> resetArguments = [tool.EntryScript, "reset"];
            session.Stage("Resetting the handset.");
            ProcessResult? reset = await session.RunAsync("reset", tool.Executable, resetArguments,
                workingDirectory, 120, ct);

            if (reset is null || !reset.Succeeded)
            {
                session.Warn("The erase succeeded but the reset command did not. Unplug the cable and hold Power for ten seconds.");
            }

            session.Warn("After the reset the handset boots into setup; verify that the Google account step is gone before closing the job.");

            return VendorSupport.Build(request.OperationType,
                session.Errors.Count == 0
                    ? $"FRP erased through the boot ROM ({partitions}) and the handset was reset."
                    : "The boot-ROM routine finished with errors.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        internal static bool IsConfirmed(OperationExecutionRequest request)
            => string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase);

        /// <summary>Only the partitions a FRP routine is allowed to touch, comma separated.</summary>
        internal static string NormalisePartitions(string? requested, bool samsung)
        {
            string[] allowed = samsung ? ["frp", "persistent", "config"] : ["frp", "persistent", "persist", "frp_p"];

            string[] asked = (requested ?? (samsung ? "frp,persistent" : "frp,persistent"))
                .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.ToLowerInvariant())
                .Select(part => allowed.Contains(part) ? part : null)
                .Where(part => part is not null)
                .Select(part => part!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return asked.Length == 0 ? "frp" : string.Join(',', asked);
        }

        internal static int ParseTimeout(string? value, int fallback)
            => int.TryParse(value, out int seconds) && seconds is >= 30 and <= 3600 ? seconds : fallback;

        /// <summary>COM3 and \\.\COM3 are both accepted; anything else is ignored.</summary>
        internal static string? NormalisePort(string? port)
        {
            if (string.IsNullOrWhiteSpace(port))
            {
                return null;
            }

            string trimmed = port.Trim();
            if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                trimmed = trimmed[4..];
            }

            return trimmed.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 3 ? trimmed : null;
        }

        private static string? FirstExisting(string? path)
            => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }
}
