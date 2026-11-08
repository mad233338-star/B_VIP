using System.Diagnostics;
using B_VIP.Contracts.Devices;
using B_VIP.Core;

namespace B_VIP.Hardware
{
    public interface IDeviceDetector
    {
        Task<DeviceListResponse> ScanAsync(CancellationToken ct = default);

        Task<DeviceInfoDto?> GetDeviceAsync(string serial, CancellationToken ct = default);

        Task<IReadOnlyList<DevicePropertyDto>> GetPropertiesAsync(string serial, CancellationToken ct = default);
    }

    /// <summary>
    /// Discovers connected handsets by asking the real tools: `adb devices -l` for devices in the
    /// system (ADB) state and `fastboot devices` for devices sitting in the bootloader. Nothing is
    /// simulated: a missing tool, an unauthorized device or a driver problem is reported as such.
    /// </summary>
    public sealed class DeviceDetector : IDeviceDetector
    {
        private readonly IAdbClient _adb;
        private readonly IToolLocator _tools;

        public DeviceDetector(IAdbClient adb, IToolLocator tools)
        {
            _adb = adb;
            _tools = tools;
        }

        public async Task<DeviceListResponse> ScanAsync(CancellationToken ct = default)
        {
            Stopwatch clock = Stopwatch.StartNew();
            List<DeviceInfoDto> devices = new();
            string? scanError = null;
            string? toolVersion = null;

            // `adb version` is only informational: the same server also answers the device listing a
            // line later, so it gets a one-second budget. A wedged or still-starting adb server used to
            // cost this scan fifteen seconds and then cancel the enumeration entirely.
            ProcessResult version = await _adb.AdbAsync(["version"], 1, ct);
            if (version.Succeeded)
            {
                toolVersion = version.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
            }
            else if (!version.Started)
            {
                // The executable itself could not be launched, so no device can be listed either.
                scanError = version.FailureReason ?? "adb could not be executed.";
            }

            if (scanError is null)
            {
                ProcessResult list = await _adb.AdbAsync(["devices", "-l"], 30, ct);
                if (list.Succeeded)
                {
                    devices.AddRange(ParseAdbDevices(list.StandardOutput));
                }
                else
                {
                    scanError = list.FailureReason ?? "adb devices -l failed.";
                }

                ProcessResult fastboot = await _adb.FastbootAsync(["devices"], 30, ct);
                if (fastboot.Succeeded)
                {
                    devices.AddRange(ParseFastbootDevices(fastboot.StandardOutput));
                }
            }

            clock.Stop();
            return new DeviceListResponse(devices, DateTime.UtcNow, clock.ElapsedMilliseconds,
                "adb/fastboot", Truncate(toolVersion, 120), scanError);
        }

        public async Task<DeviceInfoDto?> GetDeviceAsync(string serial, CancellationToken ct = default)
        {
            DeviceListResponse scan = await ScanAsync(ct);
            return scan.Devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<IReadOnlyList<DevicePropertyDto>> GetPropertiesAsync(string serial, CancellationToken ct = default)
        {
            IReadOnlyList<Dictionary<string, string>> properties = await _adb.GetPropertiesAsync(serial, ct);
            return properties
                .SelectMany(p => p)
                .Select(p => new DevicePropertyDto(p.Key, string.IsNullOrEmpty(p.Value) ? null : p.Value))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<DeviceInfoDto> ParseAdbDevices(string output)
        {
            List<DeviceInfoDto> devices = new();
            foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("*", StringComparison.Ordinal) ||
                    line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                string serial = parts[0];
                string stateToken = parts[1];
                Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
                foreach (string part in parts.Skip(2))
                {
                    int colon = part.IndexOf(':');
                    if (colon > 0)
                    {
                        tags[part[..colon]] = part[(colon + 1)..];
                    }
                }

                DeviceConnectionState state = MapAdbState(stateToken);
                string? model = tags.GetValueOrDefault("model");
                string? product = tags.GetValueOrDefault("product");
                string? device = tags.GetValueOrDefault("device");
                string? usb = tags.GetValueOrDefault("usb");
                string? transportId = tags.GetValueOrDefault("transport_id");

                devices.Add(new DeviceInfoDto(
                    DeviceId: BuildDeviceId(serial),
                    Serial: serial,
                    Manufacturer: null,
                    Model: model,
                    Product: product,
                    AndroidVersion: null,
                    OsBuild: null,
                    State: state,
                    InterfaceType: state == DeviceConnectionState.Recovery ? DeviceInterfaceType.Recovery : DeviceInterfaceType.Adb,
                    InterfaceDetail: usb is null ? "adb" : $"usb:{usb}",
                    TransportId: transportId,
                    DriverDescription: null,
                    DetectedBy: "adb devices -l",
                    DiscoveredUtc: DateTime.UtcNow,
                    Capabilities: CapabilitiesFor(state)));
            }

            return devices;
        }

        public static IReadOnlyList<DeviceInfoDto> ParseFastbootDevices(string output)
        {
            List<DeviceInfoDto> devices = new();
            foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("no permissions", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                string serial = parts[0];
                devices.Add(new DeviceInfoDto(
                    DeviceId: BuildDeviceId(serial),
                    Serial: serial,
                    Manufacturer: null,
                    Model: null,
                    Product: null,
                    AndroidVersion: null,
                    OsBuild: null,
                    State: DeviceConnectionState.Fastboot,
                    InterfaceType: DeviceInterfaceType.Fastboot,
                    InterfaceDetail: parts.Length > 1 ? parts[1] : "fastboot",
                    TransportId: null,
                    DriverDescription: null,
                    DetectedBy: "fastboot devices",
                    DiscoveredUtc: DateTime.UtcNow,
                    Capabilities: CapabilitiesFor(DeviceConnectionState.Fastboot)));
            }

            return devices;
        }

        public static string BuildDeviceId(string serial)
        {
            string normalized = new(serial.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return string.IsNullOrEmpty(normalized) ? Guid.NewGuid().ToString("N") : normalized;
        }

        private static DeviceConnectionState MapAdbState(string token) => token.ToLowerInvariant() switch
        {
            "device" => DeviceConnectionState.Connected,
            "unauthorized" => DeviceConnectionState.Unauthorized,
            "offline" => DeviceConnectionState.Disconnected,
            "recovery" => DeviceConnectionState.Recovery,
            "sideload" => DeviceConnectionState.Recovery,
            "bootloader" => DeviceConnectionState.Fastboot,
            "host" => DeviceConnectionState.Busy,
            "no device" => DeviceConnectionState.NoDevice,
            _ => DeviceConnectionState.Unsupported
        };

        private static IReadOnlyList<string> CapabilitiesFor(DeviceConnectionState state) => state switch
        {
            DeviceConnectionState.Connected => ["shell", "push", "pull", "install", "logcat", "getprop", "reboot"],
            DeviceConnectionState.Unauthorized => ["authorize_device"],
            DeviceConnectionState.Recovery => ["sideload", "reboot"],
            DeviceConnectionState.Fastboot => ["flash", "erase", "getvar", "reboot"],
            DeviceConnectionState.DriverProblem => ["install_driver"],
            _ => []
        };

        private static string? Truncate(string? value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }
}
