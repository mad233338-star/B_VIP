using System.Diagnostics;
using System.Text;

namespace B_VIP.Hardware
{
    /// <summary>
    /// Receives the command line and every output line while the tool is still running, so a caller
    /// can mirror adb/fastboot traffic into a console instead of waiting for the process to exit.
    /// </summary>
    public interface IProcessOutputSink
    {
        void Starting(string executable, IReadOnlyList<string> arguments);

        void Line(bool isError, string text);

        void Finished(int? exitCode, long durationMs);
    }

    public sealed record ProcessRequest(
        string Executable,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory = null,
        int TimeoutSeconds = 60,
        IReadOnlyDictionary<string, string>? Environment = null,
        IProcessOutputSink? OutputSink = null);

    public sealed record ProcessResult(
        string Executable,
        IReadOnlyList<string> Arguments,
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut,
        bool Cancelled,
        bool Started,
        long DurationMs,
        string? FailureReason)
    {
        public bool Succeeded => Started && !TimedOut && !Cancelled && ExitCode == 0;
    }

    public interface IProcessRunner
    {
        Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// Runs external tools without a shell: arguments are passed as an array, the executable path is
    /// validated, output is captured, and both timeout and cancellation terminate the process tree.
    /// </summary>
    public sealed class ProcessRunner : IProcessRunner
    {
        private const int OutputLimitBytes = 2_000_000;

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.Executable))
            {
                return Failed(request, "The executable path is empty.");
            }

            string? resolved = ResolveExecutable(request.Executable);
            if (resolved is null)
            {
                string display = Path.IsPathRooted(request.Executable)
                    ? Path.GetFullPath(request.Executable)
                    : request.Executable;
                return Failed(request, $"The tool was not found at '{display}'.");
            }

            string fullPath = resolved;

            if (Path.GetExtension(fullPath).Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(fullPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                return Failed(request, "Batch scripts are not executed directly for safety reasons.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = request.WorkingDirectory is not null && Directory.Exists(request.WorkingDirectory)
                    ? request.WorkingDirectory
                    : Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (request.Environment is not null)
            {
                foreach ((string key, string value) in request.Environment)
                {
                    startInfo.Environment[key] = value;
                }
            }

            var stopwatch = Stopwatch.StartNew();
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            try
            {
                if (!process.Start())
                {
                    return Failed(request, "The operating system refused to start the process.");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return Failed(request, ex.Message);
            }

            request.OutputSink?.Starting(fullPath, request.Arguments);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            Task stdoutTask = ReadAsync(process.StandardOutput, stdout, false, request.OutputSink, ct);
            Task stderrTask = ReadAsync(process.StandardError, stderr, true, request.OutputSink, ct);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds)));

            bool timedOut = false;
            bool cancelled = false;

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                cancelled = ct.IsCancellationRequested;

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception)
                {
                    // The process already exited between the check and the kill.
                }
            }

            await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask));
            stopwatch.Stop();

            int? exitCode = null;
            try
            {
                exitCode = process.HasExited ? process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                exitCode = null;
            }

            string? failure = timedOut
                ? $"The tool exceeded its {request.TimeoutSeconds}s timeout and was terminated."
                : cancelled
                    ? "The operation was cancelled by the user."
                    : null;

            SafeNotify(() => request.OutputSink?.Finished(exitCode, stopwatch.ElapsedMilliseconds));

            return new ProcessResult(
                fullPath,
                request.Arguments,
                exitCode,
                stdout.ToString(),
                stderr.ToString(),
                timedOut,
                cancelled,
                true,
                stopwatch.ElapsedMilliseconds,
                failure);
        }

        /// <summary>
        /// Resolves an executable the way an operator expects: rooted paths are used as they are,
        /// relative paths fall back to the application directory (portable installs), and bare names
        /// are searched in the application directory first and then on PATH.
        /// </summary>
        private static string? ResolveExecutable(string executable)
        {
            if (Path.IsPathRooted(executable))
            {
                return File.Exists(executable) ? Path.GetFullPath(executable) : null;
            }

            bool hasDirectoryPart = executable.IndexOf(Path.DirectorySeparatorChar) >= 0
                || executable.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

            if (hasDirectoryPart)
            {
                string local = Path.GetFullPath(executable);
                if (File.Exists(local)) return local;

                string besideApp = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, executable));
                return File.Exists(besideApp) ? besideApp : null;
            }

            string[] roots = [AppContext.BaseDirectory, Environment.CurrentDirectory];
            foreach (string root in roots)
            {
                string candidate = Path.Combine(root, executable);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path)) return null;

            bool needsExtension = Path.GetExtension(executable).Length == 0;
            foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = entry.Trim();
                if (directory.Length == 0) continue;

                try
                {
                    string candidate = Path.Combine(directory, executable);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);

                    if (needsExtension)
                    {
                        foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
                        {
                            candidate = Path.Combine(directory, executable + extension);
                            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry must not break tool resolution.
                }
            }

            return null;
        }

        /// <summary>
        /// Drains one stream line by line: the capture buffer keeps the whole text for the caller,
        /// and every complete line is forwarded to the sink as soon as it arrives.
        /// </summary>
        private static async Task ReadAsync(StreamReader reader, StringBuilder target, bool isError,
            IProcessOutputSink? sink, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null) break;

                if (target.Length < OutputLimitBytes)
                {
                    string stored = line + Environment.NewLine;
                    target.Append(stored, 0, Math.Min(stored.Length, OutputLimitBytes - target.Length));
                }

                if (!string.IsNullOrWhiteSpace(line) && sink is not null)
                {
                    SafeNotify(() => sink.Line(isError, line));
                }
            }
        }

        private static void SafeNotify(Action notify)
        {
            try
            {
                notify();
            }
            catch (Exception)
            {
                // A console sink must never break the operation it is observing.
            }
        }

        private static async Task SafeAwait(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // Output reading is best effort once the process has been terminated.
            }
        }

        private static ProcessResult Failed(ProcessRequest request, string reason) => new(
            request.Executable,
            request.Arguments,
            null,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            0,
            reason);
    }
}
