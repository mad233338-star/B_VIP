using System.Net.Http;
using System.Windows.Threading;
using B_VIP.CloudClient;
using B_VIP.Contracts.Operations;
using B_VIP.Desktop.Mvvm;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Polls the cloud service queue and publishes one honest connection state to the shell and to
    /// the Service Operations screen: connected, or still connecting.
    ///
    /// Privacy: the operator never sees where the service lives. No base address, host name, route
    /// or exception text reaches the console, the status bar or a dialog — only these two states
    /// and a task count.
    /// </summary>
    public sealed class CloudServiceMonitor : ObservableObject, IDisposable
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

        private static readonly IReadOnlyList<ServiceQueueTaskDto> NoTasks = Array.Empty<ServiceQueueTaskDto>();

        private readonly BVipApiClient _api;
        private readonly ConsoleLoggerService _log;
        private readonly DispatcherTimer? _timer;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyList<ServiceQueueTaskDto> _tasks = NoTasks;
        private bool _isConnected;
        private bool _isRefreshing;
        private DateTime? _lastSyncUtc;
        private bool _disposed;

        public CloudServiceMonitor(BVipApiClient api, ConsoleLoggerService log, Dispatcher? dispatcher = null)
        {
            _api = api;
            _log = log;

            // Without a dispatcher (tests, headless runs) the monitor still polls on demand; it
            // simply has no periodic beat and no UI to notify.
            if (dispatcher is not null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = PollInterval
                };
                _timer.Tick += async (_, _) => await RefreshAsync();
            }
        }

        /// <summary>Raised on the UI thread after every poll, including a failed one.</summary>
        public event Action? Updated;

        /// <summary>True only when the service answered a queue request with <c>status: connected</c>.</summary>
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(Detail));
                    OnPropertyChanged(nameof(IsConnecting));
                }
            }
        }

        /// <summary>The exact status-bar wording: nothing about the endpoint itself.</summary>
        public string StatusText => IsConnected ? "Cloud Service: Connected" : "Cloud Service: Connecting...";

        public bool IsConnecting => !IsConnected;

        public string Detail => IsConnected
            ? _lastSyncUtc is null
                ? "Live link to the service queue"
                : $"Last sync {_lastSyncUtc.Value.ToLocalTime():HH:mm:ss}"
            : _isRefreshing
                ? "Contacting the service..."
                : "Waiting for the service...";

        public IReadOnlyList<ServiceQueueTaskDto> Tasks => _tasks;

        public DateTime? LastSyncUtc => _lastSyncUtc;

        public bool HasTasks => _tasks.Count > 0;

        /// <summary>Starts the periodic beat. Safe to call more than once.</summary>
        public void Start() => _timer?.Start();

        public void Stop() => _timer?.Stop();

        /// <summary>
        /// Polls the queue once. Overlapping polls collapse into the one already running, so a
        /// manual "Sync now" during a tick never doubles the traffic.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            bool wasConnected = IsConnected;
            _isRefreshing = true;
            try
            {
                ServiceQueueResponse response = await _api.GetServiceQueueAsync().ConfigureAwait(false);
                bool connected = response.Success &&
                    string.Equals(response.Status, "connected", StringComparison.OrdinalIgnoreCase);

                _tasks = response.Tasks ?? NoTasks;
                _lastSyncUtc = response.GeneratedUtc ?? DateTime.UtcNow;
                Publish(connected);
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // An unreachable or refusing service is a connection state, not an error dialog.
                // The exception text is deliberately dropped: it can carry the endpoint address.
                _tasks = NoTasks;
                Publish(false);
            }
            finally
            {
                _isRefreshing = false;
                _gate.Release();
            }

            if (wasConnected != IsConnected)
            {
                _log.Info(IsConnected
                    ? $"Cloud service connected — {_tasks.Count} queued task(s)."
                    : "Cloud service is not reachable; retrying in the background.");
            }
        }

        private void Publish(bool connected)
        {
            UiThread.Invoke(() =>
            {
                IsConnected = connected;
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(HasTasks));
                Updated?.Invoke();
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _gate.Dispose();
        }
    }
}
