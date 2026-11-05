using System.IO;
using B_VIP.Hardware;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Mirrors adb/fastboot traffic into the live console while the tool is still running: the
    /// resolved command line, every stdout/stderr line as it arrives, and the exit status.
    /// </summary>
    public sealed class ConsoleProcessSink : IProcessOutputSink
    {
        private readonly IConsoleLogger _log;

        public ConsoleProcessSink(IConsoleLogger log)
        {
            _log = log;
        }

        public void Starting(string executable, IReadOnlyList<string> arguments)
        {
            string tool = Path.GetFileNameWithoutExtension(executable);
            _log.Process($"$ {tool} {string.Join(' ', arguments)}".TrimEnd());
        }

        public void Line(bool isError, string text)
        {
            // fastboot prints most of getvar to stderr while still succeeding, so stream traffic is
            // kept neutral and the final verdict is logged separately by the caller.
            _log.Write(LogLevel.Process, isError ? "stderr: " + text : text);
        }

        public void Finished(int? exitCode, long durationMs)
        {
            string status = exitCode is null ? "exit n/a" : $"exit {exitCode}";
            _log.Write(LogLevel.Process, $"… {status} in {durationMs} ms");
        }
    }
}
