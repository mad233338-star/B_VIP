using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// One external tool conversation for a vendor routine: every command line, its output and its
    /// exit status are recorded for the results table, mirrored to the operation observer (which is
    /// the live console in the desktop shell) and turned into checks the technician can read.
    ///
    /// The runner is the application's own <see cref="IProcessRunner"/>, so in the desktop build it is
    /// the streaming CLI engine: the tool's stdout/stderr arrive in the console while it runs, and
    /// the caller's <see cref="CancellationToken"/> terminates the process tree from the cancel bar.
    /// </summary>
    internal sealed class VendorCliSession
    {
        /// <summary>Output older than this many lines per command is dropped from the table.</summary>
        private const int MaxCapturedLines = 40;

        private readonly IProcessRunner _runner;
        private readonly OperationExecutionRequest _request;

        public VendorCliSession(IProcessRunner runner, OperationExecutionRequest request)
        {
            _runner = runner;
            _request = request;
        }

        public List<DiagnosticCheckDto> Checks { get; } = new();

        public List<string> Log { get; } = new();

        public List<string> Warnings { get; } = new();

        public List<string> Errors { get; } = new();

        /// <summary>True once the operator cancelled; no further command is started.</summary>
        public bool Cancelled { get; private set; }

        /// <summary>True when a command exceeded its timeout.</summary>
        public bool TimedOut { get; private set; }

        public bool HasOutputErrors => Errors.Count > 0;

        /// <summary>Writes a stage line to the console and to the results table.</summary>
        public void Stage(string message)
        {
            Log.Add(message);
            _request.Process(message);
        }

        public void Note(string message) => Log.Add(message);

        public void Warn(string message)
        {
            Warnings.Add(message);
            _request.Warning(message);
        }

        public void Check(string name, bool passed, string detail, long? durationMs = null)
            => Checks.Add(new DiagnosticCheckDto(name, passed ? "pass" : "fail", detail, durationMs));

        /// <summary>
        /// Runs one command. A missing tool, a timeout and a non-zero exit are all turned into an
        /// error with a readable sentence, and cancellation is remembered so the caller stops.
        /// </summary>
        public async Task<ProcessResult?> RunAsync(
            string step,
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            int timeoutSeconds,
            CancellationToken ct)
        {
            if (Cancelled)
            {
                return null;
            }

            string display = $"{Path.GetFileName(executable)} {string.Join(' ', arguments)}".Trim();
            _request.Process($"{step}: {display}");

            ProcessResult result = await _runner.RunAsync(
                new ProcessRequest(executable, arguments, workingDirectory, timeoutSeconds), ct);

            if (result.Cancelled)
            {
                Cancelled = true;
                Errors.Add($"{step} was cancelled.");
                Checks.Add(new DiagnosticCheckDto(step, "warn", "Cancelled by the user.", result.DurationMs));
                return result;
            }

            CaptureOutput(result);

            if (!result.Started)
            {
                string reason = string.IsNullOrWhiteSpace(result.FailureReason)
                    ? $"{step}: the tool could not be started."
                    : result.FailureReason!;
                Errors.Add(reason);
                Checks.Add(new DiagnosticCheckDto(step, "fail", reason, null));
                return result;
            }

            if (result.TimedOut)
            {
                TimedOut = true;
                string reason = result.FailureReason ?? $"{step} exceeded its {timeoutSeconds}s timeout.";
                Errors.Add(reason);
                Checks.Add(new DiagnosticCheckDto(step, "fail", reason, result.DurationMs));
                return result;
            }

            if (!result.Succeeded)
            {
                string reason = FirstLine(result.StandardError + " " + result.StandardOutput);
                if (reason.Length == 0)
                {
                    reason = $"exit code {result.ExitCode}";
                }

                Errors.Add($"{step} failed: {VendorSupport.Truncate(reason, 220)}");
                Checks.Add(new DiagnosticCheckDto(step, "fail", VendorSupport.Truncate(reason, 220), result.DurationMs));
                return result;
            }

            Checks.Add(new DiagnosticCheckDto(step, "pass", $"exit {result.ExitCode}", result.DurationMs));
            return result;
        }

        /// <summary>Adds the tail of a tool's output to the results table without flooding it.</summary>
        private void CaptureOutput(ProcessResult result)
        {
            string combined = (result.StandardOutput ?? string.Empty) + "\n" + (result.StandardError ?? string.Empty);
            string[] lines = combined
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd())
                .Where(line => line.Trim().Length > 0)
                .ToArray();

            if (lines.Length == 0)
            {
                return;
            }

            Log.Add($"  exit {result.ExitCode?.ToString() ?? "?"}:");
            foreach (string line in lines.TakeLast(MaxCapturedLines))
            {
                Log.Add("  " + VendorSupport.Truncate(line, 240));
            }
        }

        private static string FirstLine(string value) => VendorSupport.FirstLine(value);
    }
}
