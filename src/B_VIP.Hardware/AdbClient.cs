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

            // A handset prints well over a thousand of these in a few hundred milliseconds. The dump
            // is parsed line by line and *nothing* is discarded just because the line does not look
            // exactly like the usual pair, otherwise one unexpected format empties the whole table.
            return ParseGetprop(result.StandardOutput);
        }

        /// <summary>
        /// Parses an <c>adb shell getprop</c> dump. The canonical line is
        /// <c>[ro.product.model]: [M2101K6G]</c>; the bracket-less <c>key: value</c> and
        /// <c>key=value</c> shapes are accepted as well so a vendor build that prints its properties
        /// differently still fills the table. Blank values are kept as empty strings.
        /// </summary>
        public static IReadOnlyList<Dictionary<string, string>> ParseGetprop(string? output)
        {
            List<Dictionary<string, string>> properties = new(256);
            if (string.IsNullOrWhiteSpace(output))
            {
                return properties;
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0 || !TryParseProperty(line, out string name, out string value))
                {
                    continue;
                }

                if (!seen.Add(name))
                {
                    continue;
                }

                properties.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [name] = value
                });
            }

            return properties;
        }

        /// <summary>Extracts one property from one dump line without trusting the exact spacing.</summary>
        private static bool TryParseProperty(string line, out string name, out string value)
        {
            name = string.Empty;
            value = string.Empty;

            // [key]: [value] — the shape every stock Android build prints. The key can never contain
            // ']', so the first "]:" after the opening bracket ends it, and any ':' or ']' inside the
            // value is left untouched.
            if (line[0] == '[')
            {
                int keyEnd = line.IndexOf("]:", StringComparison.Ordinal);
                if (keyEnd > 1)
                {
                    name = line[1..keyEnd].Trim();
                    string rest = line[(keyEnd + 2)..].Trim();
                    if (rest.Length >= 2 && rest[0] == '[' && rest[^1] == ']')
                    {
                        rest = rest[1..^1];
                    }

                    value = rest;
                    return name.Length > 0;
                }

                // A bracketed key with an unusual separator falls through to the lenient split.
                int bracket = line.IndexOf(']');
                if (bracket > 1)
                {
                    int after = line.IndexOf(':', bracket);
                    if (after > bracket)
                    {
                        name = line[1..bracket].Trim();
                        value = line[(after + 1)..].Trim().Trim('[', ']');
                        return name.Length > 0;
                    }
                }
            }

            // key: value or key=value
            int colon = line.IndexOf(':');
            int equals = line.IndexOf('=');
            int split = colon > 0 && (equals < 0 || colon < equals) ? colon : equals;
            if (split <= 0 || split == line.Length - 1)
            {
                return false;
            }

            name = line[..split].Trim();
            value = line[(split + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
            {
                value = value[1..^1];
            }

            return name.Length > 0;
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
