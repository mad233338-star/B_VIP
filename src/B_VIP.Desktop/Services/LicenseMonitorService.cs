using System.Net.Http;
using System.Windows.Threading;
using B_VIP.CloudClient;
using B_VIP.Contracts.Auth;
using B_VIP.Desktop.Mvvm;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Keeps the signed-in session's licence honest while the tool runs.
    ///
    /// It checks once at sign-in and then on a one-minute heartbeat, so a plan that ends while the
    /// window is open locks the vendor modules at the real expiry moment instead of at the next
    /// restart. The server remains the authority: every operation is still refused there, this only
    /// decides what the shell shows and whether a device screen may start work.
    /// </summary>
    public sealed class LicenseMonitorService : ObservableObject, IDisposable
    {
        /// <summary>The exact wording of the red lockout banner.</summary>
        public const string LockoutBannerText =
            "Device operations are locked. Your trial or subscription is not active. " +
            "Renew the plan to unlock ADB, Fastboot and every vendor module.";

        private readonly BVipApiClient _api;
        private readonly SessionService _session;
        private readonly ConsoleLoggerService _log;
        private readonly DispatcherTimer? _heartbeat;
        private bool _isLocked;
        private string _statusText = "License: not checked yet";
        private string _reason = string.Empty;

        public LicenseMonitorService(
            BVipApiClient api,
            SessionService session,
            ConsoleLoggerService log,
            Dispatcher? dispatcher = null)
        {
            _api = api;
            _session = session;
            _log = log;

            Recompute();

            if (dispatcher is not null)
            {
                _heartbeat = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromMinutes(1)
                };
                _heartbeat.Tick += async (_, _) => await RefreshAsync();
            }
        }

        /// <summary>True when the licence has lapsed or is suspended: the vendor modules refuse to run.</summary>
        public bool IsLocked
        {
            get => _isLocked;
            private set
            {
                if (SetProperty(ref _isLocked, value))
                {
                    OnPropertyChanged(nameof(CanRunOperations));
                    OnPropertyChanged(nameof(BannerText));
                }
            }
        }

        public bool CanRunOperations => !_isLocked;

        /// <summary>Why the workspace is locked, or an empty string while it is open.</summary>
        public string Reason
        {
            get => _reason;
            private set => SetProperty(ref _reason, value);
        }

        /// <summary>Status-bar line, e.g. "License Active: 2 days left" or "License Active: lifetime access".</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>Empty unless the workspace is locked, so the banner disappears as soon as a plan is active.</summary>
        public string BannerText => _isLocked ? LockoutBannerText : string.Empty;

        public LicenseSummaryDto? License => _session.License;

        /// <summary>Called once the session exists; checks immediately and starts the heartbeat.</summary>
        public void Start()
        {
            Recompute();
            _heartbeat?.Start();
        }

        public void Stop() => _heartbeat?.Stop();

        /// <summary>Re-reads the licence from the server and refreshes the lock state.</summary>
        public async Task RefreshAsync()
        {
            if (!_session.IsSignedIn)
            {
                return;
            }

            bool wasLocked = _isLocked;
            try
            {
                MeResponse me = await _api.MeAsync();
                _session.License = me.License;
                _session.User = me.User;
                _session.Permissions = me.Permissions;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                // Offline: keep the last known state instead of locking the bench on a dropped Wi-Fi.
                _log.Warn($"License check failed: {ex.Message}");
            }

            Recompute();

            if (_isLocked && !wasLocked)
            {
                _log.Warn(LockoutBannerText);
            }
            else if (!_isLocked && wasLocked)
            {
                _log.Success("License is active again — device operations unlocked.");
            }
        }

        private void Recompute()
        {
            LicenseSummaryDto? license = _session.License;
            IsLocked = !LicenseGuard.CanRunOperations(license, DateTime.UtcNow, out string reason);
            Reason = reason;
            StatusText = Describe(license);
        }

        private static string Describe(LicenseSummaryDto? license)
        {
            if (license is null)
            {
                return "License: none";
            }

            if (license.ExpiresAt is null)
            {
                return "License Active: lifetime access";
            }

            TimeSpan left = license.ExpiresAt.Value - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return "License: expired";
            }

            int days = (int)left.TotalDays;
            if (days > 0)
            {
                return $"License Active: {days} day(s) left";
            }

            return left.Hours > 0
                ? $"License Active: {left.Hours} hour(s) left"
                : $"License Active: {Math.Max(1, left.Minutes)} minute(s) left";
        }

        public void Dispose()
        {
            _heartbeat?.Stop();
        }
    }
}
