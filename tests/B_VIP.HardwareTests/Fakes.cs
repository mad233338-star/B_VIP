using B_VIP.Contracts.Devices;
using B_VIP.Hardware;
using B_VIP.Modules;
using B_VIP.Modules.Samsung;

namespace B_VIP.HardwareTests
{
    /// <summary>A process runner that returns canned results instead of spawning processes.</summary>
    public sealed class StubProcessRunner : IProcessRunner
    {
        private readonly string _standardOutput;

        public StubProcessRunner(string standardOutput = "")
        {
            _standardOutput = standardOutput;
        }

        public Func<ProcessRequest, ProcessResult>? Handler { get; set; }

        public List<ProcessRequest> Requests { get; } = new();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            ProcessResult result = Handler?.Invoke(request)
                ?? new ProcessResult(request.Executable, request.Arguments, 0, _standardOutput, string.Empty,
                    false, false, true, 1, null);
            return Task.FromResult(result);
        }
    }

    /// <summary>A tool locator backed by an in-memory map of tool name to path.</summary>
    public sealed class StubToolLocator : IToolLocator
    {
        private readonly Dictionary<string, string?> _paths = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string tool, string? path) => _paths[tool] = path;

        public string? Find(string toolName) => _paths.TryGetValue(toolName, out string? path) ? path : null;

        public Task<ToolStatusDto> GetStatusAsync(string toolName, CancellationToken ct = default)
        {
            string? path = Find(toolName);
            return Task.FromResult(new ToolStatusDto(toolName, path is not null, path, path is null ? null : "stub 1.0",
                path is null ? "not installed" : null));
        }

        public Task<IReadOnlyList<ToolStatusDto>> GetAllStatusAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolStatusDto>>(Array.Empty<ToolStatusDto>());
    }

    /// <summary>An adb client whose answers the test scripts directly; no device or tool is needed.</summary>
    public sealed class FakeAdbClient : IAdbClient
    {
        public Func<IReadOnlyList<string>, ProcessResult>? AdbHandler { get; set; }

        public Func<IReadOnlyList<string>, ProcessResult>? FastbootHandler { get; set; }

        public Func<string, string, ProcessResult>? ShellHandler { get; set; }

        /// <summary>Every adb command line the code under test issued, in order.</summary>
        public List<string[]> AdbCalls { get; } = new();

        /// <summary>Every fastboot command line the code under test issued, in order.</summary>
        public List<string[]> FastbootCalls { get; } = new();

        public IReadOnlyList<Dictionary<string, string>> Properties { get; set; } = Array.Empty<Dictionary<string, string>>();

        public List<string> ShellCommands { get; } = new();

        public Task<ProcessResult> AdbAsync(IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default)
            => Task.FromResult(Invoke(arguments.ToArray()));

        public Task<ProcessResult> AdbSerialAsync(string serial, IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            List<string> all = new() { "-s", serial };
            all.AddRange(arguments);
            return Task.FromResult(Invoke(all));
        }

        public Task<ProcessResult> FastbootAsync(IEnumerable<string> arguments, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            string[] args = arguments.ToArray();
            FastbootCalls.Add(args);
            return Task.FromResult(FastbootHandler?.Invoke(args) ?? Ok(args));
        }

        public Task<IReadOnlyList<Dictionary<string, string>>> GetPropertiesAsync(string serial, CancellationToken ct = default)
            => Task.FromResult(Properties);

        public Task<ProcessResult> ShellAsync(string serial, string command, int timeoutSeconds = 60, CancellationToken ct = default)
        {
            ShellCommands.Add(command);
            ProcessResult result = ShellHandler is null ? Ok(Array.Empty<string>()) : ShellHandler(serial, command);
            return Task.FromResult(result);
        }

        public Task<bool> RebootAsync(string serial, string? target, CancellationToken ct = default) => Task.FromResult(true);

        public static ProcessResult Ok(IReadOnlyList<string> arguments, string standardOutput = "")
            => new("adb", arguments, 0, standardOutput, string.Empty, false, false, true, 1, null);

        public static ProcessResult Failed(IReadOnlyList<string> arguments, string reason)
            => new("adb", arguments, null, string.Empty, string.Empty, false, false, false, 0, reason);

        private ProcessResult Invoke(IReadOnlyList<string> arguments)
        {
            AdbCalls.Add(arguments.ToArray());
            return AdbHandler?.Invoke(arguments) ?? Ok(arguments);
        }
    }

    /// <summary>An MTP device list the test scripts directly; no portable device is needed.</summary>
    public sealed class FakeMtpProtocolService : IMtpProtocolService
    {
        public List<MtpDeviceInfo> Devices { get; } = new();

        public Task<IReadOnlyList<MtpDeviceInfo>> FindDevicesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MtpDeviceInfo>>(Devices.ToList());
    }

    /// <summary>Builds the Samsung executor with the real service graph over a fake adb client.</summary>
    public static class SamsungTestFactory
    {
        public static SamsungModuleExecutor Create(IAdbClient adb, IMtpProtocolService? mtp = null)
            => new(adb, new SamsungExecutionService(adb, mtp ?? new FakeMtpProtocolService(), new MtpBrowserService(adb)));
    }

    /// <summary>Builds a getprop style property list from "name=value" pairs.</summary>
    public static class Props
    {
        public static IReadOnlyList<Dictionary<string, string>> Of(params (string Key, string Value)[] entries)
            => entries.Select(e => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [e.Key] = e.Value }).ToList();
    }
}
