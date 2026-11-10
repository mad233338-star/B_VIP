using System.Collections.ObjectModel;
using System.Windows.Threading;
using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Desktop.Mvvm;
using B_VIP.Hardware;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Keeps the shell live: subscribes to the Windows port watcher, merges the PnP ports with a
    /// real <c>adb devices</c> / <c>fastboot devices</c> scan after every change, and reports each
    /// arrival or removal to the console. All state changes happen on the UI thread, so the status
    /// bar binds to this object without any manual refresh.
    /// </summary>
    public sealed class DeviceMonitorService : ObservableObject, IDisposable
    {
        private static readonly TimeSpan ScanDebounce = TimeSpan.FromMilliseconds(900);

        private readonly IPortWatcher _ports;
        private readonly IDeviceDetector _detector;
        private readonly IConsoleLogger _log;
        private readonly Dispatcher? _dispatcher;
        private readonly ObservableCollection<DetectedPort> _portList = new();
        private readonly ReadOnlyObservableCollection<DetectedPort> _portsView;
        private IReadOnlyList<DeviceInfoDto> _devices = Array.Empty<DeviceInfoDto>();
        private CancellationTokenSource? _pendingScan;
        private string? _scanError;
        private bool _started;
        private bool _disposed;

        public DeviceMonitorService(IPortWatcher ports, IDeviceDetector detector, IConsoleLogger log, Dispatcher? dispatcher = null)
        {
            _ports = ports;
            _detector = detector;
            _log = log;
            _dispatcher = dispatcher;
            _portsView = new ReadOnlyObservableCollection<DetectedPort>(_portList);
        }

        /// <summary>Every present USB/COM device, ordered by category then port name.</summary>
        public ReadOnlyObservableCollection<DetectedPort> Ports => _portsView;

        /// <summary>ADB and fastboot devices reported by the bundled tools.</summary>
        public IReadOnlyList<DeviceInfoDto> Devices => _devices;

        public bool IsMonitoring => _ports.IsMonitoring;

        public string? ScanError => _scanError;

        /// <summary>Compact status-bar text, for example "1 ADB · 0 Fastboot · 3 ports".</summary>
        public string Summary
        {
            get
            {
                int adb = _devices.Count(d => d.InterfaceType == DeviceInterfaceType.Adb || d.InterfaceType == DeviceInterfaceType.Recovery);
                int fastboot = _devices.Count(d => d.InterfaceType == DeviceInterfaceType.Fastboot);
                return $"{adb} ADB · {fastboot} Fastboot · {_portList.Count} ports";
            }
        }

        /// <summary>One line per port, used as the status-bar tooltip.</summary>
        public string Detail => _portList.Count == 0
            ? _ports.Warning ?? "No USB or COM device is connected."
            : string.Join(Environment.NewLine, _portList.Select(p => p.Summary));

        /// <summary>
        /// The interface the technician is most likely to act on: a download/boot mode port outranks
        /// an MTP or generic serial port, so the status bar names the mode the handset is sitting in
        /// even while the adb and fastboot daemons are idle.
        /// </summary>
        public DetectedPort? ActivePort => _portList
            .OrderBy(p => Priority(p.Category))
            .FirstOrDefault();

        /// <summary>Status-bar text such as "Connected: Samsung Download Mode (COM3)".</summary>
        public string ConnectionStatus
        {
            get
            {
                DetectedPort? port = ActivePort;
                if (port is null)
                {
                    return IsMonitoring ? "No device connected" : "Hardware monitor offline";
                }

                return port.PortName is null
                    ? $"Connected: {port.CategoryLabel}"
                    : $"Connected: {port.CategoryLabel} ({port.PortName})";
            }
        }

        /// <summary>The hardware ID behind the active interface, for the status-bar tooltip.</summary>
        public string ActivePortDetail => ActivePort is { } port
            ? $"{port.DisplayName}{Environment.NewLine}{port.HardwareId ?? port.InstanceId}"
            : Detail;

        private static int Priority(PortCategory category) => category switch
        {
            PortCategory.SamsungDownload => 0,
            PortCategory.QualcommEdl => 1,
            PortCategory.MediaTekPreloader => 2,
            PortCategory.SpreadtrumDiag => 3,
            PortCategory.Fastboot => 4,
            PortCategory.Adb => 5,
            PortCategory.MediaTekVcom => 6,
            PortCategory.SamsungMtp => 7,
            PortCategory.Mtp => 8,
            PortCategory.Serial => 9,
            _ => 10
        };

        /// <summary>
        /// Re-enumerates every USB/COM interface now and writes each one to the live console. This
        /// is the hardware half of "scan for devices": it works in download modes where neither adb
        /// nor fastboot answers.
        /// </summary>
        public async Task<int> ScanInterfacesAsync(CancellationToken ct = default)
        {
            PortSnapshot snapshot;
            try
            {
                // WMI enumeration is a blocking COM call: keep it off the caller's thread.
                snapshot = await Task.Run(() => _ports.Refresh(), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"The hardware scan failed: {ex.Message}");
                return 0;
            }

            ApplyPorts(snapshot.Ports);

            _log.Info($"Hardware scan complete — {snapshot.Count} USB/COM interface(s) detected.");
            if (snapshot.Count == 0)
            {
                _log.Info("No interface is present. Connect the handset in ADB, fastboot, Download, EDL, preloader or Diag mode.");
            }
            else
            {
                foreach (DetectedPort port in snapshot.Ports)
                {
                    _log.Info($"  {port.CategoryLabel}: {port.DisplayName}" +
                              (port.PortName is null ? string.Empty : $" on {port.PortName}"));
                }
            }

            if (snapshot.Warning is not null)
            {
                _log.Warn(snapshot.Warning);
            }

            return snapshot.Count;
        }

        public void Start()
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _ports.DeviceConnectionChanged += OnPortsChanged;

            // WMI initialisation and the first enumeration take a few hundred milliseconds: keep them
            // off the startup path of the window. A failure here must reach the console instead of
            // disappearing into an unobserved task.
            _ = Task.Run(() =>
            {
                try
                {
                    _ports.Start();
                    ApplyPorts(_ports.Current.Ports);
                    OnPropertyChanged(nameof(IsMonitoring));
                    OnPropertyChanged(nameof(Detail));
                    OnPropertyChanged(nameof(ConnectionStatus));

                    if (_ports.Warning is not null)
                    {
                        _log.Warn(_ports.Warning);
                    }
                    else
                    {
                        _log.Info($"Port monitor ready — {_ports.Current.Count} USB/COM device(s) detected.");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"Port monitoring could not start: {ex.Message}");
                }
            });

            _ = RefreshDevicesAsync();
        }

        private void OnPortsChanged(object? sender, PortChangeEventArgs e)
        {
            ApplyPorts(e.Snapshot.Ports);

            foreach (DetectedPort port in e.Added)
            {
                _log.Success($"Device connected: {port.Summary}");

                // Emergency download mode is the one arrival an operator has to act on within seconds:
                // the handset only stays there briefly, so it is called out by name.
                if (port.Category == PortCategory.QualcommEdl)
                {
                    _log.Info($"Qualcomm 9008 port detected on {port.PortName ?? "an unnamed interface"}.");
                }
            }

            foreach (DetectedPort port in e.Removed)
            {
                _log.Info($"Device disconnected: {port.Summary}");
            }

            OnPropertyChanged(nameof(Detail));
            ScheduleDeviceScan();
        }

        private void ScheduleDeviceScan()
        {
            CancellationTokenSource cts = new();
            CancellationTokenSource? previous = Interlocked.Exchange(ref _pendingScan, cts);
            previous?.Cancel();
            previous?.Dispose();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ScanDebounce, cts.Token);
                    await RefreshDevicesAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // A newer change superseded this scan.
                }
                finally
                {
                    if (ReferenceEquals(Interlocked.CompareExchange(ref _pendingScan, null, cts), cts))
                    {
                        cts.Dispose();
                    }
                }
            });
        }

        private async Task RefreshDevicesAsync(CancellationToken ct = default)
        {
            try
            {
                DeviceListResponse response = await _detector.ScanAsync(ct);
                ApplyDevices(response.Devices, response.ScanError);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ApplyDevices(Array.Empty<DeviceInfoDto>(), ex.Message);
                _log.Error($"Device scan failed: {ex.Message}");
            }
        }

        private void ApplyPorts(IReadOnlyList<DetectedPort> ports)
        {
            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => ApplyPorts(ports)));
                return;
            }

            _portList.Clear();
            foreach (DetectedPort port in ports)
            {
                _portList.Add(port);
            }

            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(ActivePort));
            OnPropertyChanged(nameof(ActivePortDetail));
            OnPropertyChanged(nameof(ConnectionStatus));
        }

        private void ApplyDevices(IReadOnlyList<DeviceInfoDto> devices, string? error)
        {
            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => ApplyDevices(devices, error)));
                return;
            }

            _devices = devices;
            _scanError = error;
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(ScanError));
        }

        public void Stop()
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _ports.DeviceConnectionChanged -= OnPortsChanged;

            CancellationTokenSource? pending = Interlocked.Exchange(ref _pendingScan, null);
            pending?.Cancel();
            pending?.Dispose();

            _ports.Stop();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _ports.Dispose();
        }
    }
}
