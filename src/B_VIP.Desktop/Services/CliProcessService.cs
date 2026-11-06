using System.IO;
using B_VIP.Hardware;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// The one place external command-line tools are executed for the desktop application.
    ///
    /// It decorates a <see cref="IProcessRunner"/> and mirrors every adb/fastboot invocation into the
    /// live console while it runs: the command line as <c>[PROCESS]</c>, stdout as <c>[INFO]</c>,
    /// stderr as <c>[ERROR]</c> and the exit status as <c>[SUCCESS]</c> (or <c>[ERROR]</c> when the
    /// tool reported failure). The caller's <see cref="CancellationToken"/> is passed straight
    /// through, so the shell's cancel button terminates the process tree instead of freezing the UI
    /// thread while it waits for a handset.
    ///
    /// Because every adb/fastboot call in the application (device scan, modules, vendor tabs) is
    /// routed through the runner registered with the composition root, wiring this class once is
    /// enough to capture the whole CLI surface.
    /// </summary>
    public sealed class CliProcessService : IProcessRunner
    {
        private readonly IProcessRunner _inner;
        private readonly IConsoleLogger _log;

        public CliProcessService(IProcessRunner inner, IConsoleLogger log)
        {
            _inner = inner;
            _log = log;
        }

        /// <summary>Runs a tool, streaming its output to the live console.</summary>
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            // The device monitor lists attached handsets every few seconds; echoing those polls
            // would bury real work in the console, so they run silently.
            if (!IsStreamed(request))
            {
                return _inner.RunAsync(request, ct);
            }

            ProcessRequest streamed = request with
            {
                OutputSink = new ConsoleSink(_log, request.OutputSink)
            };

            return _inner.RunAsync(streamed, ct);
        }

        public async Task<ProcessResult> AdbAsync(IEnumerable<string> arguments, int timeoutSeconds = 60,
            CancellationToken ct = default)
            => await RunAsync(new ProcessRequest(ToolLocator.Adb, arguments.ToArray(), TimeoutSeconds: timeoutSeconds), ct);

        public async Task<ProcessResult> FastbootAsync(IEnumerable<string> arguments, int timeoutSeconds = 60,
            CancellationToken ct = default)
            => await RunAsync(new ProcessRequest(ToolLocator.Fastboot, arguments.ToArray(), TimeoutSeconds: timeoutSeconds), ct);

        /// <summary>
        /// True for the argument sets that describe a scan rather than work: the monitor polls
        /// <c>adb devices</c> and <c>fastboot devices</c> continuously.
        /// </summary>
        private static bool IsStreamed(ProcessRequest request)
        {
            IReadOnlyList<string> arguments = request.Arguments;
            if (arguments.Count == 0)
            {
                return true;
            }

            string first = arguments[0];
            if (string.Equals(first, "devices", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "start-server", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "kill-server", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // "adb -s <serial> devices" is still a listing.
            return !(arguments.Count >= 3 && arguments.Contains("devices", StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Bridges the process streams to the console. The command line is written once, each line
        /// keeps the severity of the stream it arrived on, and the exit status closes the block.
        /// </summary>
        private sealed class ConsoleSink : IProcessOutputSink
        {
            private readonly IConsoleLogger _log;
            private readonly IProcessOutputSink? _downstream;

            public ConsoleSink(IConsoleLogger log, IProcessOutputSink? downstream)
            {
                _log = log;
                _downstream = downstream;
            }

            public void Starting(string executable, IReadOnlyList<string> arguments)
            {
                string tool = Path.GetFileNameWithoutExtension(executable);
                _log.Process($"$ {tool} {string.Join(' ', arguments)}".TrimEnd());
                _downstream?.Starting(executable, arguments);
            }

            public void Line(bool isError, string text)
            {
                // fastboot writes successful getvar output to stderr, so the stream is coloured by
                // origin and the verdict below is what the technician should trust.
                _log.Write(isError ? LogLevel.Error : LogLevel.Info, isError ? "stderr: " + text : text);
                _downstream?.Line(isError, text);
            }

            public void Finished(int? exitCode, long durationMs)
            {
                string status = exitCode is null ? "exit n/a" : $"exit {exitCode}";
                if (exitCode == 0)
                {
                    _log.Success($"{status} in {durationMs} ms");
                }
                else
                {
                    _log.Error($"{status} in {durationMs} ms");
                }

                _downstream?.Finished(exitCode, durationMs);
            }
        }
    }
}
