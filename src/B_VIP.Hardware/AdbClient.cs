using System.Text.Json;
using B_VIP.Contracts.Devices;

namespace B_VIP.Hardware
{
    /// <summary>Thin, typed wrapper over the adb and fastboot executables.</summary>
    public interface IAdbClient
    {
        Task<ProcessResult> AdbAsync(IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default);

        Task<ProcessResult> AdbSerialAsync(string serial, IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default);

        Task<ProcessResult> FastbootAsync(IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default);

        Task<IReadOnlyList<Dictionary<string, string>>> GetPropertiesAsync(string serial, CancellationToken ct = default);

        Task<ProcessResult> ShellAsync(string serial, string command, int timeoutSeconds = 60, CancellationToken ct = default);

        Task<bool> RebootAsync(string serial, string? target, CancellationToken ct = default);
    }

    public sealed class AdbClient : IAdbClient
    {
        private readonly IToolLocator _tools;
        private readonly IProcessRunner _runner;
        private readonly IProcessOutputSink? _outputSink;

        public AdbClient(IToolLocator tools, IProcessRunner runner, IProcessOutputSink? outputSink = null)
        {
            _tools = tools;
            _runner = runner;
            _outputSink = outputSink;
        }

        public async Task<ProcessResult> AdbAsync(IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            string? adb = _tools.Find(ToolLocator.Adb);
            if (adb is null)
            {
                return NotInstalled(ToolLocator.Adb);
            }

            return await _runner.RunAsync(
                new ProcessRequest(adb, arguments.ToArray(), TimeoutSeconds: timeoutSeconds, OutputSink: _outputSink), ct);
        }

        public Task<ProcessResult> AdbSerialAsync(string serial, IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            List<string> args = ["-s", serial];
            args.AddRange(arguments);
            return AdbAsync(args, timeoutSeconds, ct);
        }

        public async Task<ProcessResult> FastbootAsync(IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            string? fastboot = _tools.Find(ToolLocator.Fastboot);
            if (fastboot is null)
            {
                return NotInstalled(ToolLocator.Fastboot);
            }

            return await _runner.RunAsync(
                new ProcessRequest(fastboot, arguments.ToArray(), TimeoutSeconds: timeoutSeconds, OutputSink: _outputSink), ct);
        }

        public async Task<IReadOnlyList<Dictionary<string, string>>> GetPropertiesAsync(string serial, CancellationToken ct = default)
        {
            ProcessResult result = await AdbSerialAsync(serial, ["shell", "getprop"], timeoutSeconds: 45, ct);
            if (!result.Succeeded)
            {
                return Array.Empty<Dictionary<string, string>>();
            }

            List<Dictionary<string, string>> properties = new(128);
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int close = line.LastIndexOf(']');
                int open = line.IndexOf('[');
                if (open <= 1 || close < open)
                {
                    continue;
                }

                string name = line[1..open];
                string value = line[(open + 1)..close];
                if (name.Length > 0)
                {
                    properties.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [name] = value
                    });
                }
            }

            return properties;
        }

        public Task<ProcessResult> ShellAsync(string serial, string command, int timeoutSeconds = 60, CancellationToken ct = default)
            => AdbSerialAsync(serial, ["shell", command], timeoutSeconds, ct);

        public async Task<bool> RebootAsync(string serial, string? target, CancellationToken ct = default)
        {
            List<string> args = ["reboot"];
            if (!string.IsNullOrWhiteSpace(target))
            {
                args.Add(target);
            }

            ProcessResult result = await AdbSerialAsync(serial, args, 30, ct);
            return result.Succeeded;
        }

        private static ProcessResult NotInstalled(string tool)
            => new(tool, Array.Empty<string>(), null, string.Empty, string.Empty, false, false, false, 0,
                $"The '{tool}' executable was not found. Place {tool}.exe in the " +
                $"{ToolLocator.PlatformToolsFolder} folder next to the application and retry.");
    }
}
