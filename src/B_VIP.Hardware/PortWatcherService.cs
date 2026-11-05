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
            string? service = null, string? manufacturer = null, DateTime? detectedUtc = null)
        {
            string haystack = string.Join(' ',
                new[] { name, description, service, manufacturer }.Where(s => !string.IsNullOrWhiteSpace(s)))
                .ToLowerInvariant();

            PortCategory category = ResolveCategory(haystack);
            string? port = ExtractPortName(name) ?? ExtractPortName(description);

            return new DetectedPort(
                instanceId,
                string.IsNullOrWhiteSpace(name) ? description ?? instanceId : name!,
                port,
                category,
                ResolveVendor(haystack),
                detectedUtc ?? DateTime.UtcNow);
        }

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
            "SELECT DeviceID, Name, Description, Service, Manufacturer FROM Win32_PnPEntity";

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
                debounce = _debounce;
                poll = _poll;
                _arrival = null;
                _removal = null;
                _debounce = null;
                _poll = null;
            }

            DisposeWatcher(arrival);
            DisposeWatcher(removal);
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

                    if (!PortClassifier.IsRelevant(instanceId, name ?? description, service))
                    {
                        continue;
                    }

                    ports.Add(PortClassifier.Classify(instanceId!, name, description, service, manufacturer));
                }
            }

            return ports;
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
