using System.IO;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// Qualcomm EDL firmware flashing through <c>fh_loader.exe</c>, the same bundled tool the FRP
    /// routine uses. The operator supplies the three pieces a Firehose package needs — the programmer
    /// for this SoC, <c>rawprogram0.xml</c> and <c>patch0.xml</c> — and fh_loader writes the images
    /// the XML names, then resets.
    ///
    /// The process is started hidden and its output streams into the console, so no vendor window
    /// ever appears; the run is bounded by the request timeout and can be cancelled from the UI.
    /// </summary>
    public sealed class QualcommFirmwareExecutor : IOperationExecutor
    {
        private readonly IProcessRunner _runner;
        private readonly IVendorToolchain _toolchain;

        public QualcommFirmwareExecutor(IProcessRunner runner, IVendorToolchain? toolchain = null)
        {
            _runner = runner;
            _toolchain = toolchain ?? new VendorToolchain();
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.QualcommFlashFirmware];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            VendorCliSession session = new(_runner, request);

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                session.Errors.Add("Flashing firmware must be confirmed explicitly before it runs.");
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

            string? port = FhLoaderFrpExecutor.NormalisePort(request.GetParameter("port"))
                           ?? FhLoaderFrpExecutor.NormalisePort(request.Serial);
            if (port is null)
            {
                const string detail = "No 9008 COM port was selected. Put the handset in EDL mode and refresh the interface list.";
                session.Errors.Add(detail);
                session.Checks.Add(new DiagnosticCheckDto("edl-port", "fail", detail, null));
                return VendorSupport.Build(request.OperationType, "No EDL port selected.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("edl-port", true, $"EDL port: {port}");

            string? programmer = FirstExisting(request.GetParameter("firehosePath"))
                                 ?? _toolchain.FindFirehoseProgrammer();
            string? rawProgram = FirstExisting(request.GetParameter("rawProgram"));
            string? patch = FirstExisting(request.GetParameter("patch"));

            if (rawProgram is null)
            {
                const string detail = "Select the rawprogram0.xml that came with this firmware package.";
                session.Errors.Add(detail);
                session.Checks.Add(new DiagnosticCheckDto("rawprogram", "fail", detail, null));
                return VendorSupport.Build(request.OperationType, "No rawprogram XML selected.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("rawprogram", true, $"rawprogram: {Path.GetFileName(rawProgram)}");

            if (patch is not null)
            {
                session.Check("patch", true, $"patch: {Path.GetFileName(patch)}");
            }
            else
            {
                session.Warn("No patch0.xml was selected. Images are written exactly as the rawprogram XML names them, " +
                             "without the post-flash patches some packages need.");
            }

            // The images sit beside the XML, so that folder is what fh_loader searches.
            string searchPath = Path.GetDirectoryName(rawProgram)!;
            List<string> arguments = BuildArguments(port, rawProgram, patch, searchPath, programmer);

            if (programmer is not null)
            {
                session.Note($"Programmer: {Path.GetFileName(programmer)}");
            }
            else
            {
                session.Warn("No programmer was selected; fh_loader will use the first prog_firehose_*.mbn it finds in " +
                             "its own folder, which may not match this SoC.");
            }

            int timeout = FhLoaderFrpExecutor.ParseTimeout(request.GetParameter("timeoutSeconds"), 3600);

            session.Stage($"Flashing through EDL on {port} (up to {timeout}s).");
            ProcessResult? result = await session.RunAsync("edl-flash", tool.Executable, arguments,
                Path.GetDirectoryName(tool.Executable), timeout, ct);

            if (result is null || !result.Succeeded || session.Cancelled)
            {
                if (!session.Cancelled)
                {
                    session.Warn("A flash that stops part way leaves the handset unbootable. Re-run the same package " +
                                 "with the handset still in EDL; do not unplug it.");
                }

                return VendorSupport.Build(request.OperationType,
                    session.Cancelled ? "Cancelled." : "The EDL flash did not complete.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("edl-flash", true, "fh_loader reported success.");
            session.Warn("Wait for the handset to boot by itself; the first boot after a full flash takes several minutes.");

            return VendorSupport.Build(request.OperationType,
                $"Firmware flashed on {port} and the handset was reset.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        /// <summary>
        /// Builds the fh_loader command line: one <c>--sendxml</c> per XML, the shared search path for
        /// the images, and a reset so the handset leaves EDL when the flash ends.
        /// </summary>
        public static List<string> BuildArguments(string port, string rawProgram, string? patch,
            string searchPath, string? programmer)
        {
            List<string> arguments =
            [
                $"--port=\\\\.\\{port.Trim()}",
                $"--sendxml=\"{Path.GetFileName(rawProgram)}\"",
                $"--search_path=\"{searchPath}\"",
                "--noprompt",
                "--showpercentagecomplete",
                "--zlpawarehost=1",
                "--reset"
            ];

            if (!string.IsNullOrWhiteSpace(patch))
            {
                // patch0.xml must be applied after the program XML, hence the ordering.
                arguments.Add($"--sendxml=\"{Path.GetFileName(patch)}\"");
            }

            if (!string.IsNullOrWhiteSpace(programmer))
            {
                arguments.Add($"--memoryname={MemoryNameFor(programmer)}");
            }

            return arguments;
        }

        /// <summary>
        /// UFS and eMMC packages differ in how the programmer addresses storage. An eMMC programmer or
        /// a small (<64 MiB) package is treated as eMMC; everything else is UFS, which is the modern default.
        /// </summary>
        public static string MemoryNameFor(string? programmer)
            => programmer is not null && programmer.Contains("emmc", StringComparison.OrdinalIgnoreCase)
                ? "emmc"
                : "ufs";

        private static string? FirstExisting(string? path)
            => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }
}
