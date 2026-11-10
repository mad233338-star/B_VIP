using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace B_VIP.Hardware
{
    /// <summary>The kind of hardware interface a port represents, in the order the tool cares about.</summary>
    public enum PortCategory
    {
        Adb,
        Fastboot,
        SamsungDownload,
        MediaTekPreloader,
        MediaTekVcom,
        QualcommEdl,
        SpreadtrumDiag,
        SamsungMtp,
        Mtp,
        Serial,
        Unknown
    }

    /// <summary>One present USB/COM device as Windows reports it.</summary>
    public sealed record DetectedPort(
        string InstanceId,
        string FriendlyName,
        string? PortName,
        PortCategory Category,
        string? Vendor,
        DateTime DetectedUtc)
    {
        /// <summary>The USB hardware ID Windows reports, for example <c>USB\VID_05C6&amp;PID_9008</c>.</summary>
        public string? HardwareId { get; init; }

        public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? PortName ?? InstanceId : FriendlyName;

        public string CategoryLabel => PortClassifier.Label(Category);

        /// <summary>Short single-line description used by the status bar and the console.</summary>
        public string Summary => PortName is null ? $"{DisplayName} [{CategoryLabel}]" : $"{DisplayName} [{CategoryLabel}] {PortName}";
    }

    /// <summary>Everything currently plugged in, plus why monitoring may be unavailable.</summary>
    public sealed record PortSnapshot(
        IReadOnlyList<DetectedPort> Ports,
        DateTime TakenUtc,
        bool IsMonitoring,
        string? Warning)
    {
        public static readonly PortSnapshot Empty = new(Array.Empty<DetectedPort>(), DateTime.UtcNow, false, null);

        public int Count => Ports.Count;

        public int CountOf(PortCategory category) => Ports.Count(p => p.Category == category);
    }

    /// <summary>Raised once per debounced change cycle with the exact difference to the previous snapshot.</summary>
    public sealed record PortChangeEventArgs(
        IReadOnlyList<DetectedPort> Added,
        IReadOnlyList<DetectedPort> Removed,
        PortSnapshot Snapshot);

    public interface IPortWatcher : IDisposable
    {
        /// <summary>Fires on a background thread whenever a USB/COM device arrives or leaves.</summary>
        event EventHandler<PortChangeEventArgs>? DeviceConnectionChanged;

        PortSnapshot Current { get; }

        bool IsMonitoring { get; }

        string? Warning { get; }

        void Start();

        void Stop();

        /// <summary>Forces a re-enumeration now and reports any difference.</summary>
        PortSnapshot Refresh();
    }

    /// <summary>
    /// Maps a Windows PnP device to a tool category. Kept separate from the WMI plumbing so the
    /// classification is pure and unit-testable: no device is ever guessed from a port number.
    /// </summary>
    public static class PortClassifier
    {
        private static readonly Regex ComPortPattern = new(@"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VendorIdPattern = new(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ProductIdPattern = new(@"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Reads the USB vendor ID (VID_xxxx) out of a PnP instance or hardware ID.</summary>
        public static string? ParseVendorId(string? id) => Match(VendorIdPattern, id);

        /// <summary>Reads the USB product ID (PID_xxxx) out of a PnP instance or hardware ID.</summary>
        public static string? ParseProductId(string? id) => Match(ProductIdPattern, id);

        private static string? Match(Regex pattern, string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            Match match = pattern.Match(id);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        public static string? ExtractPortName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            Match match = ComPortPattern.Match(text);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        public static bool IsRelevant(string? instanceId, string? name, string? service)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return false;
            }

            if (instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                instanceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (service is not null &&
                (service.Equals("usbser", StringComparison.OrdinalIgnoreCase) ||
                 service.Equals("usbmodem", StringComparison.OrdinalIgnoreCase) ||
                 service.Equals("mtkusbser", StringComparison.OrdinalIgnoreCase) ||
                 service.Equals("qcser", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return ExtractPortName(name) is not null;
        }

        public static DetectedPort Classify(string instanceId, string? name, string? description = null,
            string? service = null, string? manufacturer = null, DateTime? detectedUtc = null,
            string? hardwareId = null)
        {
            string haystack = string.Join(' ',
                new[] { name, description, service, manufacturer }.Where(s => !string.IsNullOrWhiteSpace(s)))
                .ToLowerInvariant();

            // The PnP instance ID always carries VID/PID, even when Windows publishes no friendly
            // name, so it is the fallback source for the hardware identity.
            string? vendorId = ParseVendorId(hardwareId) ?? ParseVendorId(instanceId);
            string? productId = ParseProductId(hardwareId) ?? ParseProductId(instanceId);

            PortCategory category = ResolveByHardwareId(vendorId, productId, haystack) ?? ResolveCategory(haystack);
            string? port = ExtractPortName(name) ?? ExtractPortName(description);

            return new DetectedPort(
                instanceId,
                string.IsNullOrWhiteSpace(name) ? description ?? instanceId : name!,
                port,
                category,
                ResolveVendor(haystack) ?? VendorFromId(vendorId),
                detectedUtc ?? DateTime.UtcNow)
            {
                HardwareId = string.IsNullOrWhiteSpace(hardwareId) ? instanceId : hardwareId
            };
        }

        /// <summary>
        /// Classifies a port from its USB vendor/product IDs, which is what Windows actually knows
        /// before a driver supplies a name. Returns null for a vendor that carries no single mode
        /// (a generic Android phone), so the name-based pass still decides. VID_0000 is the test and
        /// placeholder value and is ignored.
        /// </summary>
        private static PortCategory? ResolveByHardwareId(string? vendorId, string? productId, string haystack)
        {
            if (vendorId is null or "0000")
            {
                return null;
            }

            // An explicitly named interface wins: a Samsung ADB interface is an ADB port,
            // not an MTP device, whatever the vendor ID says.
            if (haystack.Contains("adb", StringComparison.Ordinal))
            {
                return PortCategory.Adb;
            }

            if (haystack.Contains("fastboot", StringComparison.Ordinal) ||
                haystack.Contains("bootloader", StringComparison.Ordinal))
            {
                return PortCategory.Fastboot;
            }

            return vendorId switch
            {
                // Samsung Download (Odin) mode is the modem/serial personality PID_685D.
                "04E8" => productId == "685D" ? PortCategory.SamsungDownload : PortCategory.SamsungMtp,
                "05C6" => productId == "9008" ? PortCategory.QualcommEdl : null,
                "0E8D" => productId is "2000" or "2001" or "0003"
                    ? PortCategory.MediaTekPreloader
                    : PortCategory.MediaTekVcom,
                "1782" => PortCategory.SpreadtrumDiag,
                // Google / Xiaomi bootloader interfaces are the ones with a bootloader product ID.
                "18D1" => productId switch
                {
                    "4EE7" => PortCategory.Adb,
                    "D00D" => PortCategory.Fastboot,
                    _ => null
                },
                "2717" => productId is "FF40" or "FF48" ? PortCategory.Fastboot : null,
                _ => null
            };
        }

        private static string? VendorFromId(string? vendorId) => vendorId switch
        {
            "04E8" => "Samsung",
            "05C6" => "Qualcomm",
            "0E8D" => "MediaTek",
            "1782" => "Unisoc",
            "18D1" => "Google",
            "2717" => "Xiaomi",
            _ => null
        };

        private static PortCategory ResolveCategory(string haystack)
        {
            // Qualcomm emergency download mode is the most specific signature: check it first so a
            // "Qualcomm HS-USB Diagnostics" port is never mistaken for a generic serial port.
            if (haystack.Contains("qdloader", StringComparison.Ordinal) ||
                haystack.Contains("9008", StringComparison.Ordinal) ||
                haystack.Contains("qualcomm", StringComparison.Ordinal))
            {
                return PortCategory.QualcommEdl;
            }

            if (haystack.Contains("sprd", StringComparison.Ordinal) ||
                haystack.Contains("spreadtrum", StringComparison.Ordinal) ||
                haystack.Contains("unisoc", StringComparison.Ordinal))
            {
                return PortCategory.SpreadtrumDiag;
            }

            // MediaTek exposes two different interfaces; the preloader is what brom/flash tools need.
            if (haystack.Contains("preloader", StringComparison.Ordinal))
            {
                return PortCategory.MediaTekPreloader;
            }

            if (haystack.Contains("mediatek", StringComparison.Ordinal) ||
                haystack.Contains("mtk", StringComparison.Ordinal) ||
                haystack.Contains("vcom", StringComparison.Ordinal))
            {
                return PortCategory.MediaTekVcom;
            }

            // ADB wins over the vendor name: a Samsung phone in ADB mode is an ADB device.
            if (haystack.Contains("adb", StringComparison.Ordinal))
            {
                return PortCategory.Adb;
            }

            if (haystack.Contains("fastboot", StringComparison.Ordinal) ||
                haystack.Contains("bootloader", StringComparison.Ordinal))
            {
                return PortCategory.Fastboot;
            }

            if (haystack.Contains("samsung", StringComparison.Ordinal) &&
                (haystack.Contains("mobile", StringComparison.Ordinal) ||
                 haystack.Contains("mtp", StringComparison.Ordinal) ||
                 haystack.Contains("modem", StringComparison.Ordinal) ||
                 haystack.Contains("composite", StringComparison.Ordinal)))
            {
                return PortCategory.SamsungMtp;
            }

            if (haystack.Contains("mtp", StringComparison.Ordinal) ||
                haystack.Contains("media transfer", StringComparison.Ordinal))
            {
                return PortCategory.Mtp;
            }

            if (haystack.Contains("(com", StringComparison.Ordinal) ||
                haystack.Contains("serial", StringComparison.Ordinal))
            {
                return PortCategory.Serial;
            }

            return PortCategory.Unknown;
        }

        private static string? ResolveVendor(string haystack)
        {
            (string Token, string Vendor)[] map =
            [
                ("samsung", "Samsung"),
                ("xiaomi", "Xiaomi"),
                ("redmi", "Xiaomi"),
                ("poco", "Xiaomi"),
                ("infinix", "Infinix"),
                ("tecno", "Tecno"),
                ("qualcomm", "Qualcomm"),
                ("mediatek", "MediaTek"),
                ("mtk", "MediaTek"),
                ("sprd", "Unisoc"),
                ("unisoc", "Unisoc"),
                ("spreadtrum", "Unisoc"),
                ("google", "Google")
            ];

            foreach ((string token, string vendor) in map)
            {
                if (haystack.Contains(token, StringComparison.Ordinal))
                {
                    return vendor;
                }
            }

            return null;
        }

        public static string Label(PortCategory category) => category switch
        {
            PortCategory.Adb => "ADB",
            PortCategory.Fastboot => "Fastboot",
            PortCategory.SamsungDownload => "Samsung Download Mode",
            PortCategory.MediaTekPreloader => "MTK Preloader",
            PortCategory.MediaTekVcom => "MTK VCOM",
            PortCategory.QualcommEdl => "Qualcomm EDL 9008",
            PortCategory.SpreadtrumDiag => "Unisoc Diag",
            PortCategory.SamsungMtp => "Samsung MTP",
            PortCategory.Mtp => "MTP",
            PortCategory.Serial => "Serial",
            _ => "USB"
        };
    }

    /// <summary>
    /// Watches Windows Plug-and-Play for USB/COM arrivals and removals through WMI
    /// (<c>Win32_PnPEntity</c> plus the instance creation/deletion events) and falls back to a
    /// timed re-enumeration when WMI monitoring is unavailable. Callers never poll: they subscribe
    /// to <see cref="DeviceConnectionChanged"/>.
    /// </summary>
    public sealed class PortWatcherService : IPortWatcher
    {
        private const string EntityQuery =
            "SELECT DeviceID, Name, Description, Service, Manufacturer, HardwareID FROM Win32_PnPEntity";

        private const string ArrivalQuery =
            "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity'";

        private const string RemovalQuery =
            "SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity'";

        private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

        private readonly object _gate = new();
        private readonly Func<IReadOnlyList<DetectedPort>> _reader;
        private ManagementEventWatcher? _arrival;
        private ManagementEventWatcher? _removal;
        private DeviceChangeWatcher? _deviceChanges;
        private Timer? _debounce;
        private Timer? _poll;
        private bool _started;
        private bool _disposed;
        private PortSnapshot _current = PortSnapshot.Empty;

        public PortWatcherService()
            : this(QueryPresentDevices)
        {
        }

        /// <summary>Test seam: the enumeration source can be replaced without touching WMI.</summary>
        internal PortWatcherService(Func<IReadOnlyList<DetectedPort>> reader)
        {
            _reader = reader;
        }

        public event EventHandler<PortChangeEventArgs>? DeviceConnectionChanged;

        public PortSnapshot Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public bool IsMonitoring => Current.IsMonitoring;

        public string? Warning => Current.Warning;

        public void Start()
        {
            lock (_gate)
            {
                if (_started || _disposed)
                {
                    return;
                }

                _started = true;
            }

            if (!OperatingSystem.IsWindows())
            {
                lock (_gate)
                {
                    _current = new PortSnapshot(Array.Empty<DetectedPort>(), DateTime.UtcNow, false,
                        "USB port monitoring is only available on Windows.");
                }

                return;
            }

            // Seed the snapshot silently: the first read is the baseline, not a "device connected" event.
            PortSnapshot baseline = BuildSnapshot(true, null);
            lock (_gate)
            {
                _current = baseline;
            }

            StartWmiWatchers();
            StartDeviceChangeWatcher();

            lock (_gate)
            {
                if (_arrival is null)
                {
                    // WMI could not be started (service disabled, permissions, ...): poll instead so
                    // the UI still updates without the operator refreshing anything.
                    _poll = new Timer(_ => Refresh(), null, PollInterval, PollInterval);
                }
            }
        }

        public void Stop()
        {
            ManagementEventWatcher? arrival;
            ManagementEventWatcher? removal;
            DeviceChangeWatcher? deviceChanges;
            Timer? debounce;
            Timer? poll;

            lock (_gate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
                arrival = _arrival;
                removal = _removal;
                deviceChanges = _deviceChanges;
                debounce = _debounce;
                poll = _poll;
                _arrival = null;
                _removal = null;
                _deviceChanges = null;
                _debounce = null;
                _poll = null;
            }

            DisposeWatcher(arrival);
            DisposeWatcher(removal);

            if (OperatingSystem.IsWindows())
            {
                deviceChanges?.Dispose();
            }

            debounce?.Dispose();
            poll?.Dispose();

            lock (_gate)
            {
                _current = _current with { IsMonitoring = false };
            }
        }

        public PortSnapshot Refresh()
        {
            PortSnapshot previous;
            lock (_gate)
            {
                previous = _current;
                if (_disposed)
                {
                    return previous;
                }
            }

            PortSnapshot next = BuildSnapshot(previous.IsMonitoring, previous.Warning);
            PortSnapshot current;
            lock (_gate)
            {
                current = _current = next;
            }

            Report(previous, current);
            return current;
        }

        private void StartWmiWatchers()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                ManagementEventWatcher arrival = CreateWatcher(ArrivalQuery);
                ManagementEventWatcher removal = CreateWatcher(RemovalQuery);
                arrival.Start();
                removal.Start();
                _arrival = arrival;
                _removal = removal;
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                _arrival?.Dispose();
                _removal?.Dispose();

                lock (_gate)
                {
                    _arrival = null;
                    _removal = null;
                    _current = _current with
                    {
                        Warning = $"WMI device notifications are unavailable ({ex.Message}); polling every {PollInterval.TotalSeconds:0}s instead."
                    };
                }
            }
        }

        /// <summary>
        /// Starts the WM_DEVICECHANGE listener. It is a bonus path: when Windows refuses the window
        /// (or the platform is not Windows) the WMI watchers and the polling fallback still run.
        /// </summary>
        private void StartDeviceChangeWatcher()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                DeviceChangeWatcher watcher = new(ScheduleRefresh);
                if (watcher.Start())
                {
                    lock (_gate)
                    {
                        _deviceChanges = watcher;
                    }
                }
                else
                {
                    watcher.Dispose();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                // No WM_DEVICECHANGE hook: WMI notifications remain the primary source.
            }
        }

        [SupportedOSPlatform("windows")]
        private ManagementEventWatcher CreateWatcher(string query)
        {
            ManagementEventWatcher watcher = new(new WqlEventQuery(query));
            watcher.EventArrived += OnWmiEvent;
            return watcher;
        }

        private void OnWmiEvent(object sender, EventArrivedEventArgs e) => ScheduleRefresh();

        private void ScheduleRefresh()
        {
            lock (_gate)
            {
                if (!_started || _disposed)
                {
                    return;
                }

                // USB enumeration produces a burst of events per device; coalesce them into one scan.
                _debounce ??= new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
                _debounce.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
            }
        }

        private PortSnapshot BuildSnapshot(bool monitoring, string? warning)
        {
            IReadOnlyList<DetectedPort> ports;
            try
            {
                ports = _reader() ?? Array.Empty<DetectedPort>();
            }
            catch (Exception ex)
            {
                // This runs on a WMI callback and on a timer: an enumeration failure must degrade to
                // an empty snapshot with a warning, never crash the host application.
                ports = Array.Empty<DetectedPort>();
                warning = $"The USB device list could not be read ({ex.Message}).";
            }

            List<DetectedPort> ordered = ports
                .OrderBy(p => p.Category)
                .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PortSnapshot(ordered, DateTime.UtcNow, monitoring, warning);
        }

        private void Report(PortSnapshot previous, PortSnapshot current)
        {
            HashSet<string> before = previous.Ports.Select(p => p.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> after = current.Ports.Select(p => p.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<DetectedPort> added = current.Ports.Where(p => !before.Contains(p.InstanceId)).ToList();
            List<DetectedPort> removed = previous.Ports.Where(p => !after.Contains(p.InstanceId)).ToList();

            if (added.Count == 0 && removed.Count == 0)
            {
                return;
            }

            DeviceConnectionChanged?.Invoke(this, new PortChangeEventArgs(added, removed, current));
        }

        private static void DisposeWatcher(ManagementEventWatcher? watcher)
        {
            if (watcher is null)
            {
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex) when (ex is ManagementException or COMException or InvalidOperationException)
            {
                // The WMI service may already be gone at shutdown.
            }
            finally
            {
                watcher.Dispose();
            }
        }

        private static IReadOnlyList<DetectedPort> QueryPresentDevices()
        {
            List<DetectedPort> ports = new();
            if (!OperatingSystem.IsWindows())
            {
                return ports;
            }

            using ManagementObjectSearcher searcher = new(EntityQuery);
            using ManagementObjectCollection devices = searcher.Get();

            foreach (ManagementBaseObject device in devices)
            {
                using (device)
                {
                    string? instanceId = device["DeviceID"]?.ToString();
                    string? name = device["Name"]?.ToString();
                    string? description = device["Description"]?.ToString();
                    string? service = device["Service"]?.ToString();
                    string? manufacturer = device["Manufacturer"]?.ToString();
                    string? hardwareId = ReadHardwareId(device);

                    if (!PortClassifier.IsRelevant(instanceId, name ?? description, service))
                    {
                        continue;
                    }

                    ports.Add(PortClassifier.Classify(instanceId!, name, description, service, manufacturer,
                        hardwareId: hardwareId));
                }
            }

            return ports;
        }

        /// <summary>Reads the first hardware ID; WMI exposes it as an array of strings.</summary>
        [SupportedOSPlatform("windows")]
        private static string? ReadHardwareId(ManagementBaseObject device)
        {
            try
            {
                object? raw = device["HardwareID"];
                if (raw is string[] ids && ids.Length > 0)
                {
                    return ids[0];
                }

                if (raw is not null)
                {
                    string? text = raw.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }

                return device["DeviceID"]?.ToString();
            }
            catch (ManagementException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }
    }
}
