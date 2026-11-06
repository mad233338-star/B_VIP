using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using B_VIP.CloudClient;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Devices;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.ViewModels
{

    /// <summary>One navigation entry: it carries the localized label and the selected flag the sidebar binds to.</summary>
    public sealed class NavItem : ObservableObject
    {
        private readonly Action<string>? _select;
        private bool _isSelected;

        public NavItem(string moduleKey, string labelKey, string group, Action<string>? select = null)
        {
            ModuleKey = moduleKey;
            LabelKey = labelKey;
            Group = group;
            _select = select;
        }

        public string ModuleKey { get; }

        public string LabelKey { get; }

        public string Group { get; }

        public string Label => Localizer.Instance[LabelKey];

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value) && value)
                {
                    _select?.Invoke(ModuleKey);
                }
            }
        }
    }

    public sealed record NavGroup(string Key, IReadOnlyList<NavItem> Items)
    {
        public string Label => Localizer.Instance["group." + Key];
    }

    public sealed class MainViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private readonly DispatcherTimer _resendCooldown;
        private string _selectedModule = "dashboard";
        private IReadOnlyList<NavGroup> _groups = Array.Empty<NavGroup>();
        private int _resendSeconds;

        public MainViewModel()
        {
            _services = AppServices.Current;

            LiveLog = new ConsoleViewModel(_services.Log);
            ToggleConsoleCommand = new RelayCommand(_ => IsConsoleVisible = !IsConsoleVisible);

            LogoutCommand = new AsyncRelayCommand(LogoutAsync);
            ResendVerificationCommand = new AsyncRelayCommand(_ => ResendVerificationAsync(), _ => !IsBusy && _resendSeconds == 0);

            _resendCooldown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _resendCooldown.Tick += (_, _) =>
            {
                if (_resendSeconds > 0)
                {
                    _resendSeconds--;
                    OnPropertyChanged(nameof(ResendLabel));
                    ResendVerificationCommand.RaiseCanExecuteChanged();
                }
                else
                {
                    _resendCooldown.Stop();
                }
            };

            Navigation = _services.Navigation;
            Navigation.Navigated += key =>
            {
                SelectedModule = key;
                OnPropertyChanged(nameof(IsAdmin));
                OnPropertyChanged(nameof(IsWorkspaceEnabled));
            };

            _services.License.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsWorkspaceEnabled));
                OnPropertyChanged(nameof(License));
            };

            foreach (Core.Modules.ModuleDefinition module in Core.Modules.ModuleCatalog.All)
            {
                Navigation.Register(module.Key, () => CreateViewModel(module.Key));
            }

            RebuildGroups();
            Navigation.NavigateTo("dashboard");
            SelectCurrent();
        }

        public NavigationService Navigation { get; }

        /// <summary>The live console drawer under the workspace.</summary>
        public ConsoleViewModel LiveLog { get; }

        /// <summary>Shell-wide progress bar and cancel action, shared with every screen.</summary>
        public OperationProgressViewModel Progress => _services.Progress;

        /// <summary>Live USB/COM and adb/fastboot state for the status bar.</summary>
        public DeviceMonitorService Monitor => _services.Monitor;

        /// <summary>Trial/subscription state: remaining time, lock flag and the lockout banner.</summary>
        public LicenseMonitorService License => _services.License;

        /// <summary>
        /// False while the trial or subscription has lapsed, so every device screen is disabled at
        /// once. The License &amp; Activation screen stays reachable through the banner so the
        /// technician can renew or paste a key.
        /// </summary>
        public bool IsWorkspaceEnabled =>
            !_services.License.IsLocked ||
            string.Equals(SelectedModule, "license-activation", StringComparison.OrdinalIgnoreCase);

        /// <summary>Sends a locked technician to the renewal screen.</summary>
        public RelayCommand RenewCommand => _renewCommand ??= new RelayCommand(_ =>
            SelectedModule = "license-activation");

        private RelayCommand? _renewCommand;

        public RelayCommand ToggleConsoleCommand { get; }

        private bool _isConsoleVisible = true;

        public bool IsConsoleVisible
        {
            get => _isConsoleVisible;
            set
            {
                if (SetProperty(ref _isConsoleVisible, value))
                {
                    OnPropertyChanged(nameof(ConsoleToggleLabel));
                }
            }
        }

        public string ConsoleToggleLabel => IsConsoleVisible ? "Hide console" : "Console";

        public string this[string key] => Localizer.Instance[key];

        public bool IsRightToLeft => Localizer.Instance.IsRightToLeft;

        public string UserName => _services.Session.DisplayName;

        public bool IsAdmin => _services.Session.IsAdmin;

        public IReadOnlyList<NavGroup> Groups => _groups;

        public string SelectedModule
        {
            get => _selectedModule;
            set
            {
                if (SetProperty(ref _selectedModule, value) && !string.IsNullOrEmpty(value))
                {
                    Navigation.NavigateTo(value);
                    SelectCurrent();
                }
            }
        }

        public AsyncRelayCommand LogoutCommand { get; }

        public AsyncRelayCommand ResendVerificationCommand { get; }

        /// <summary>True while the signed-in technician still has to activate the e-mail address.</summary>
        public bool ShowVerificationBanner => _services.Session.User is { EmailVerified: false };

        public string VerificationBannerText =>
            $"Your e-mail address is not verified. Activate the link sent to {_services.Session.User?.Email} to unlock subscriptions and licensing.";

        /// <summary>Resend button caption: a countdown keeps the button disabled for 60 seconds.</summary>
        public string ResendLabel => _resendSeconds > 0
            ? Localizer.Instance.Format("verify.wait", _resendSeconds)
            : Localizer.Instance["verify.resend"];

        private async Task ResendVerificationAsync()
        {
            if (_resendSeconds > 0)
            {
                return;
            }

            string? email = _services.Session.User?.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                return;
            }

            IsBusy = true;
            try
            {
                var response = await _services.Api.ResendVerificationAsync(email);
                StatusMessage = response.Message;
                ErrorMessage = null;
                StartResendCooldown();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void StartResendCooldown()
        {
            _resendSeconds = 60;
            OnPropertyChanged(nameof(ResendLabel));
            ResendVerificationCommand.RaiseCanExecuteChanged();
            _resendCooldown.Start();
        }

        public override Task OnNavigatedToAsync()
        {
            OnPropertyChanged(nameof(UserName));
            OnPropertyChanged(nameof(IsAdmin));
            return Task.CompletedTask;
        }

        private void RebuildGroups()
        {
            // The administration suite is its own window; it is never a page of the technician shell,
            // so there are no view-switcher buttons between the two surfaces.
            _groups = Core.Modules.ModuleCatalog.All
                .Where(m => !string.Equals(m.Key, "admin-dashboard", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.Group)
                .Select(g => new NavGroup(g.Key,
                    g.Select(m => new NavItem(m.Key, ModuleLabelKey(m.Key), g.Key, key => SelectedModule = key)).ToList()))
                .ToList();
            OnPropertyChanged(nameof(Groups));
            SelectCurrent();
        }

        private void SelectCurrent()
        {
            foreach (NavGroup group in _groups)
            {
                foreach (NavItem item in group.Items)
                {
                    item.IsSelected = string.Equals(item.ModuleKey, _selectedModule, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static string ModuleLabelKey(string moduleKey) => moduleKey switch
        {
            "dashboard" => "nav.dashboard",
            "device-manager" => "nav.devices",
            "android-diagnostics" => "nav.diagnostics",
            Core.ModuleKeys.Adb => "nav.adb",
            Core.ModuleKeys.Fastboot => "nav.fastboot",
            "samsung" => "nav.samsung",
            "xiaomi" => "nav.xiaomi",
            "infinix" => "nav.infinix",
            "teco" => "nav.teco",
            "mediatek" => "nav.mediatek",
            "qualcomm" => "nav.qualcomm",
            "spreadtrum" => "nav.spreadtrum",
            Core.ModuleKeys.Drivers => "nav.drivers",
            "google-recovery" => "nav.google",
            "service-operations" => "nav.operations",
            "diagnostic-reports" => "nav.reports",
            "license-activation" => "nav.license",
            "support-center" => "nav.support",
            "notifications" => "nav.notifications",
            Core.ModuleKeys.Terms => "nav.terms",
            Core.ModuleKeys.Privacy => "nav.privacy",
            "settings" => "nav.settings",
            "about" => "nav.about",
            "admin-dashboard" => "nav.admin",
            _ => moduleKey
        };

        private ViewModelBase CreateViewModel(string moduleKey) => moduleKey switch
        {
            "dashboard" => new DashboardViewModel(),
            "device-manager" => new DeviceManagerViewModel(),
            "android-diagnostics" => new DiagnosticsViewModel(),
            Core.ModuleKeys.Adb => new AdbViewModel(),
            Core.ModuleKeys.Fastboot => new FastbootViewModel(),
            "samsung" => new SamsungViewModel(),
            "xiaomi" => new VendorViewModel(VendorKind.Xiaomi),
            "infinix" => new VendorViewModel(VendorKind.Infinix),
            "teco" => new VendorViewModel(VendorKind.Tecno),
            "mediatek" => new MtkViewModel(),
            "qualcomm" => new QualcommViewModel(),
            "spreadtrum" => new SpdViewModel(),
            Core.ModuleKeys.Drivers => new DriversViewModel(),
            "google-recovery" => new GoogleRecoveryViewModel(),
            "service-operations" => new OperationsViewModel(),
            "diagnostic-reports" => new ReportsViewModel(),
            "license-activation" => new SubscriptionViewModel(),
            "support-center" => new SupportViewModel(),
            "notifications" => new NotificationsViewModel(),
            Core.ModuleKeys.Terms => new TermsViewModel(),
            Core.ModuleKeys.Privacy => new PrivacyPolicyViewModel(),
            "settings" => new SettingsViewModel(),
            "about" => new AboutViewModel(),
            "admin-dashboard" => new AdminViewModel(),
            _ => new DashboardViewModel()
        };

        private async Task LogoutAsync(object? _)
        {
            await _services.Api.LogoutAsync();
            _services.Session.Clear();
            System.Windows.Application.Current.Shutdown();
        }
    }

    public sealed class DashboardViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private readonly DispatcherTimer _countdown;
        private string _deviceSummary = "—";
        private string _toolSummary = "—";
        private string _operationSummary = "—";
        private string _licenseSummary = "—";
        private IReadOnlyList<string> _recentOperations = Array.Empty<string>();
        private bool _isLicenseModalOpen;

        public DashboardViewModel()
        {
            _services = AppServices.Current;
            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
            ScanCommand = new AsyncRelayCommand(async _ =>
            {
                await _services.Devices.ScanAsync();
                await RefreshAsync();
            });
            NavigateCommand = new RelayCommand(key =>
                _services.Navigation.NavigateTo(key?.ToString() ?? "dashboard"));

            OpenLicenseModalCommand = new AsyncRelayCommand(_ => OpenLicenseModalAsync());
            CloseLicenseModalCommand = new RelayCommand(_ => IsLicenseModalOpen = false);
            RenewCommand = new RelayCommand(_ =>
            {
                IsLicenseModalOpen = false;
                _services.Navigation.NavigateTo("license-activation");
            });

            // The remaining-time line ticks once a minute so an open dashboard stays truthful.
            _countdown = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _countdown.Tick += (_, _) => RefreshLicenseState();
            _countdown.Start();
        }

        public string this[string key] => Localizer.Instance[key];

        public AsyncRelayCommand RefreshCommand { get; }

        public AsyncRelayCommand ScanCommand { get; }

        public RelayCommand NavigateCommand { get; }

        public AsyncRelayCommand OpenLicenseModalCommand { get; }

        public RelayCommand CloseLicenseModalCommand { get; }

        public RelayCommand RenewCommand { get; }

        /// <summary>The in-place license sheet on the dashboard (no separate window).</summary>
        public bool IsLicenseModalOpen
        {
            get => _isLicenseModalOpen;
            private set => SetProperty(ref _isLicenseModalOpen, value);
        }

        public string LicensePlanName => _services.Session.License?.PlanName ?? "No subscription";

        public string LicenseStatusText
        {
            get
            {
                Contracts.Auth.LicenseSummaryDto? license = _services.Session.License;
                if (license is null)
                {
                    return "None";
                }

                if (license.ExpiresAt.HasValue && license.ExpiresAt.Value <= DateTime.UtcNow)
                {
                    return "Expired";
                }

                return string.Equals(license.PlanCode, Core.Rules.LicenseRules.TrialPlanCode, StringComparison.OrdinalIgnoreCase)
                    ? $"Trial · {license.Status}"
                    : license.Status;
            }
        }

        public string LicenseCountdown => LicenseGuard.DescribeRemaining(_services.Session.License, DateTime.UtcNow);

        public string LicenseHardwareId => string.IsNullOrWhiteSpace(_services.Session.User?.HardwareId)
            ? "Not bound yet (binds on the next sign-in)"
            : _services.Session.User!.HardwareId!;

        public string LicenseDevicesText
        {
            get
            {
                Contracts.Auth.LicenseSummaryDto? license = _services.Session.License;
                return license is null
                    ? "—"
                    : $"{license.CurrentActivations} of {license.MaxActivations} workstation(s) in use";
            }
        }

        public bool IsLicenseLocked => !LicenseGuard.CanRunOperations(_services.Session.License, out _);

        private async Task OpenLicenseModalAsync()
        {
            IsBusy = true;
            try
            {
                // Re-read the license from the server so an administrator's change shows up at once.
                Contracts.Auth.MeResponse me = await _services.Api.MeAsync();
                _services.Session.License = me.License;
                _services.Session.User = me.User;
                _services.Session.Permissions = me.Permissions;
            }
            catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
                RefreshLicenseState();
                IsLicenseModalOpen = true;
            }
        }

        private void RefreshLicenseState()
        {
            OnPropertyChanged(nameof(LicensePlanName));
            OnPropertyChanged(nameof(LicenseStatusText));
            OnPropertyChanged(nameof(LicenseCountdown));
            OnPropertyChanged(nameof(LicenseHardwareId));
            OnPropertyChanged(nameof(LicenseDevicesText));
            OnPropertyChanged(nameof(IsLicenseLocked));
        }

        public string WelcomeMessage => Localizer.Instance.Format("dashboard.welcome", _services.Session.DisplayName);

        public string DeviceSummary
        {
            get => _deviceSummary;
            private set => SetProperty(ref _deviceSummary, value);
        }

        public string ToolSummary
        {
            get => _toolSummary;
            private set => SetProperty(ref _toolSummary, value);
        }

        public string OperationSummary
        {
            get => _operationSummary;
            private set => SetProperty(ref _operationSummary, value);
        }

        public string LicenseSummary
        {
            get => _licenseSummary;
            private set => SetProperty(ref _licenseSummary, value);
        }

        public IReadOnlyList<string> RecentOperations
        {
            get => _recentOperations;
            private set => SetProperty(ref _recentOperations, value);
        }

        public override async Task OnNavigatedToAsync() => await RefreshAsync();

        private async Task RefreshAsync()
        {
            IsBusy = true;
            try
            {
                IReadOnlyList<ToolStatusDto> tools = await _services.Devices.GetToolStatusAsync();
                ToolSummary = $"{tools.Count(t => t.Found)}/{tools.Count}";

                Contracts.Auth.LicenseSummaryDto? license = _services.Session.License;
                LicenseSummary = license is null
                    ? "—"
                    : $"{license.PlanName} · {LicenseStatusText}";
                RefreshLicenseState();

                OperationSummary = _services.Devices.History.Count.ToString();
                RecentOperations = _services.Devices.History
                    .Take(6)
                    .Select(o => $"[{o.OccurredUtc:HH:mm}] {o.OperationType} — {o.Outcome}")
                    .ToList();

                DeviceSummary = "…";
                DeviceListResponse scan = await _services.Devices.ScanAsync();
                DeviceSummary = scan.Devices.Count.ToString();
                ErrorMessage = scan.ScanError;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
