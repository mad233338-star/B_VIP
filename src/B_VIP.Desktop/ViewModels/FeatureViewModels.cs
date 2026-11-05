using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using B_VIP.CloudClient;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Licenses;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Reports;
using B_VIP.Contracts.Tickets;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Mvvm;
using B_VIP.Modules;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.ViewModels
{
    public sealed class OperationsViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private string _selectedOperationType;
        private string _parametersJson = "{}";
        private bool _confirmDataLoss;

        public OperationsViewModel()
        {
            _services = AppServices.Current;
            OperationTypes = Core.Operations.OperationTypes.All
                .Select(o => o.Key)
                .ToList();
            _selectedOperationType = OperationTypes.FirstOrDefault() ?? string.Empty;

            LocalHistory = new ObservableCollection<LocalOperation>();
            ServerOperations = new ObservableCollection<OperationDto>();

            RunCommand = new AsyncRelayCommand(_ => RunAsync());
            RefreshCommand = new AsyncRelayCommand(_ => LoadServerAsync());
        }

        public string this[string key] => Localizer.Instance[key];

        public IReadOnlyList<string> OperationTypes { get; }

        public ObservableCollection<LocalOperation> LocalHistory { get; }

        public ObservableCollection<OperationDto> ServerOperations { get; }

        public AsyncRelayCommand RunCommand { get; }

        public AsyncRelayCommand RefreshCommand { get; }

        public string SelectedOperationType
        {
            get => _selectedOperationType;
            set => SetProperty(ref _selectedOperationType, value);
        }

        public string ParametersJson
        {
            get => _parametersJson;
            set => SetProperty(ref _parametersJson, value);
        }

        public bool ConfirmDataLoss
        {
            get => _confirmDataLoss;
            set => SetProperty(ref _confirmDataLoss, value);
        }

        public override async Task OnNavigatedToAsync()
        {
            ReloadLocal();
            await LoadServerAsync();
        }

        private void ReloadLocal()
        {
            LocalHistory.Clear();
            foreach (LocalOperation operation in _services.Devices.History)
            {
                LocalHistory.Add(operation);
            }
        }

        private async Task RunAsync()
        {
            ErrorMessage = null;
            IReadOnlyDictionary<string, string> parameters = ParseParameters();

            try
            {
                OperationExecutionResult result = await _services.Devices.ExecuteAsync(
                    SelectedOperationType,
                    serial: null,
                    deviceId: null,
                    deviceDescription: null,
                    parameters,
                    ConfirmDataLoss);

                StatusMessage = result.Summary;
                ErrorMessage = result.Errors.Count > 0 ? string.Join(Environment.NewLine, result.Errors) : null;
                ReloadLocal();
                await LoadServerAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private IReadOnlyDictionary<string, string> ParseParameters()
        {
            try
            {
                Dictionary<string, string>? values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    string.IsNullOrWhiteSpace(ParametersJson) ? "{}" : ParametersJson,
                    new JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                return values ?? new Dictionary<string, string>();
            }
            catch (System.Text.Json.JsonException)
            {
                ErrorMessage = "Parameters must be a JSON object of string values, for example {\"partition\":\"boot\"}.";
                return new Dictionary<string, string>();
            }
        }

        private async Task LoadServerAsync()
        {
            try
            {
                OperationListResponse response = await _services.Api.SearchOperationsAsync(null, null, null, 1, 25);
                ServerOperations.Clear();
                foreach (OperationDto operation in response.Items)
                {
                    ServerOperations.Add(operation);
                }
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable; only local results are shown.";
            }
        }
    }

    public sealed class ReportsViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private DiagnosticReportDto? _selectedReport;

        public ReportsViewModel()
        {
            _services = AppServices.Current;
            Reports = new ObservableCollection<DiagnosticReportDto>();
            SearchCommand = new AsyncRelayCommand(_ => LoadAsync());
            ExportCommand = new AsyncRelayCommand(_ => ExportAsync());
        }

        public string this[string key] => Localizer.Instance[key];

        public ObservableCollection<DiagnosticReportDto> Reports { get; }

        public AsyncRelayCommand SearchCommand { get; }

        public AsyncRelayCommand ExportCommand { get; }

        public string? Filter { get; set; }

        public DiagnosticReportDto? SelectedReport
        {
            get => _selectedReport;
            set => SetProperty(ref _selectedReport, value, () => OnPropertyChanged(nameof(ReportContent)));
        }

        public string ReportContent => _selectedReport?.ContentJson ?? string.Empty;

        public override async Task OnNavigatedToAsync() => await LoadAsync();

        private async Task LoadAsync()
        {
            IsBusy = true;
            ErrorMessage = null;
            try
            {
                ReportListResponse response = await _services.Api.SearchReportsAsync(Filter, null, 1, 50);
                Reports.Clear();
                foreach (DiagnosticReportDto report in response.Items)
                {
                    Reports.Add(report);
                }

                StatusMessage = $"{response.TotalCount} report(s).";
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private Task ExportAsync()
        {
            if (SelectedReport is null)
            {
                ErrorMessage = "Select a report first.";
                return Task.CompletedTask;
            }

            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "B_VIP");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"report-{SelectedReport.Id}.json");
                string json = SelectedReport.Redacted ? SelectedReport.ContentJson : ReportRedactor.Redact(SelectedReport.ContentJson);
                File.WriteAllText(path, json);
                StatusMessage = path;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>One subscription card: the real plan data plus the capabilities it unlocks.</summary>
    public sealed record SubscriptionTier(
        string Code,
        string Name,
        string Billing,
        string Features,
        string PriceText,
        string DurationText,
        string DevicesText,
        bool IsCurrent);

    public sealed class LicenseViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private string _licenseKey = string.Empty;
        private VerifyLicenseResponse? _verification;
        private LicenseSummaryDto? _current;
        private SubscriptionTier? _selectedTier;
        private string _paymentNote = string.Empty;

        public LicenseViewModel()
        {
            _services = AppServices.Current;
            _current = _services.Session.License;
            _licenseKey = _current?.LicenseKey ?? string.Empty;

            VerifyCommand = new AsyncRelayCommand(_ => VerifyAsync());
            ActivateCommand = new AsyncRelayCommand(_ => ActivateAsync());
            DeactivateCommand = new AsyncRelayCommand(_ => DeactivateAsync());
            SelectTierCommand = new RelayCommand(SelectTier);
            Plans = new ObservableCollection<LicensePlanDto>();
            Activations = new ObservableCollection<ActivationDto>();
            Tiers = new ObservableCollection<SubscriptionTier>();
        }

        public ObservableCollection<SubscriptionTier> Tiers { get; }

        public RelayCommand SelectTierCommand { get; }

        public SubscriptionTier? SelectedTier
        {
            get => _selectedTier;
            set => SetProperty(ref _selectedTier, value, () => OnPropertyChanged(nameof(HasSelectedTier)));
        }

        public bool HasSelectedTier => _selectedTier is not null;

        public string PaymentNote
        {
            get => _paymentNote;
            private set => SetProperty(ref _paymentNote, value);
        }

        public string DaysRemaining => _current?.ExpiresAt is null
            ? "No expiry (lifetime)."
            : $"{Math.Max(0, (int)Math.Ceiling((_current.ExpiresAt.Value - DateTime.UtcNow).TotalDays))} day(s) remaining.";

        public string CurrentTierLabel => _current is null
            ? "No active subscription"
            : $"{_current.PlanName} · {_current.Status}";

        public string this[string key] => Localizer.Instance[key];

        public ObservableCollection<LicensePlanDto> Plans { get; }

        public ObservableCollection<ActivationDto> Activations { get; }

        public AsyncRelayCommand VerifyCommand { get; }

        public AsyncRelayCommand ActivateCommand { get; }

        public AsyncRelayCommand DeactivateCommand { get; }

        public LicenseSummaryDto? CurrentLicense
        {
            get => _current;
            set => SetProperty(ref _current, value, () => OnPropertyChanged(nameof(Summary)));
        }

        public string Summary => _current is null
            ? "No license is attached to this account."
            : $"{_current.PlanName} · {_current.Status} · {_current.CurrentActivations}/{_current.MaxActivations}";

        public string LicenseKey
        {
            get => _licenseKey;
            set => SetProperty(ref _licenseKey, value);
        }

        public VerifyLicenseResponse? Verification
        {
            get => _verification;
            set => SetProperty(ref _verification, value, () => OnPropertyChanged(nameof(VerificationText)));
        }

        public string VerificationText => _verification is null
            ? string.Empty
            : $"{_verification.Result}: {_verification.Message}";

        public override async Task OnNavigatedToAsync()
        {
            try
            {
                MeResponse me = await _services.Api.MeAsync();
                CurrentLicense = me.License;
                _services.Session.License = me.License;

                IReadOnlyList<LicensePlanDto> plans = await _services.Api.GetPlansAsync();
                Plans.Clear();
                foreach (LicensePlanDto plan in plans)
                {
                    Plans.Add(plan);
                }

                BuildTiers();
                OnPropertyChanged(nameof(DaysRemaining));
                OnPropertyChanged(nameof(CurrentTierLabel));

                if (me.License is not null)
                {
                    LicenseActivationListResponse activations = await _services.Api.GetActivationsAsync(me.License.LicenseId);
                    Activations.Clear();
                    foreach (ActivationDto activation in activations.Activations)
                    {
                        Activations.Add(activation);
                    }
                }
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
        }

        private async Task VerifyAsync()
        {
            try
            {
                Verification = await _services.Api.VerifyLicenseAsync(new VerifyLicenseRequest(
                    LicenseKey, Fingerprint(), ClientVersion(), RecordEvent: true));
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
        }

        private async Task ActivateAsync()
        {
            try
            {
                ActivateLicenseResponse response = await _services.Api.ActivateLicenseAsync(new ActivateLicenseRequest(
                    LicenseKey, Fingerprint(), Environment.MachineName, "windows-x64", ClientVersion()));
                StatusMessage = response.Message;
                ErrorMessage = response.Success ? null : response.Message;
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
        }

        private async Task DeactivateAsync()
        {
            try
            {
                await _services.Api.DeactivateLicenseAsync(new DeactivateLicenseRequest(Fingerprint(), "manual"));
                StatusMessage = "This device was deactivated.";
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
        }

        /// <summary>Turns the server's real plans into the three subscription cards the client shows.</summary>
        private void BuildTiers()
        {
            Tiers.Clear();

            foreach (LicensePlanDto plan in Plans.OrderBy(p => p.SortOrder))
            {
                (string name, string billing, string features) = plan.Code.ToLowerInvariant() switch
                {
                    "monthly" => ("Standard Technician", "Monthly",
                        "Device diagnostics · FRP and partition tools · ADB and Fastboot · one workstation"),
                    "quarterly" => ("Workshop Pro", "Quarterly / semi-annual",
                        "Everything in Standard · Samsung, Xiaomi, Infinix, Tecno, MediaTek, Qualcomm and SPREADTRUM modules · Google account recovery"),
                    "annual" => ("VIP Enterprise", "Annual",
                        "Everything in Workshop Pro · every chipset module · highest concurrent-PC limit · direct support channel"),
                    _ => (plan.Name, plan.DurationDays is null ? "Custom term" : $"{plan.DurationDays} days",
                        string.IsNullOrWhiteSpace(plan.Description) ? plan.Name : plan.Description)
                };

                string price = plan.Price.HasValue
                    ? $"{plan.Price.Value:0.##} {plan.Currency ?? "USD"}"
                    : "Price on request";

                string duration = plan.IsLifetime
                    ? "Lifetime"
                    : plan.DurationDays is null ? "Custom" : $"{plan.DurationDays} days";

                Tiers.Add(new SubscriptionTier(
                    plan.Code,
                    name,
                    billing,
                    features,
                    price,
                    duration,
                    $"{plan.MaxActivations} concurrent PC(s)",
                    string.Equals(_current?.PlanCode, plan.Code, StringComparison.OrdinalIgnoreCase)));
            }

            SelectedTier ??= Tiers.FirstOrDefault(t => t.IsCurrent) ?? Tiers.FirstOrDefault();
        }

        private void SelectTier(object? parameter)
        {
            if (parameter is not SubscriptionTier tier)
            {
                return;
            }

            SelectedTier = tier;

            LicensePlanDto? plan = Plans.FirstOrDefault(p =>
                string.Equals(p.Code, tier.Code, StringComparison.OrdinalIgnoreCase));

            PaymentNote = plan is null
                ? string.Empty
                : $"{tier.Name} · {tier.PriceText} · {tier.DevicesText}. " +
                  "Send your username and this tier to the administrator; you will receive a BVIP-XXXX-XXXX-XXXX activation key. " +
                  "Paste the key above and press Activate.";
        }

        internal static string Fingerprint()
        {
            string raw = $"{Environment.MachineName}:{Environment.UserName}:{Environment.ProcessorCount}";
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        }

        internal static string ClientVersion()
            => typeof(LicenseViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    public sealed class SupportViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private TicketDetailDto? _selectedTicket;
        private string _newTitle = string.Empty;
        private string _newDescription = string.Empty;
        private string _newPriority = "Normal";
        private string _replyBody = string.Empty;

        public SupportViewModel()
        {
            _services = AppServices.Current;
            Tickets = new ObservableCollection<TicketDto>();
            SearchCommand = new AsyncRelayCommand(_ => LoadAsync());
            CreateCommand = new AsyncRelayCommand(_ => CreateAsync());
            ReplyCommand = new AsyncRelayCommand(_ => ReplyAsync());
            Priorities = new[] { "Low", "Normal", "High", "Urgent" };
        }

        public string this[string key] => Localizer.Instance[key];

        public IReadOnlyList<string> Priorities { get; }

        public ObservableCollection<TicketDto> Tickets { get; }

        public AsyncRelayCommand SearchCommand { get; }

        public AsyncRelayCommand CreateCommand { get; }

        public AsyncRelayCommand ReplyCommand { get; }

        public TicketDetailDto? SelectedTicket
        {
            get => _selectedTicket;
            set => SetProperty(ref _selectedTicket, value, () => OnPropertyChanged(nameof(Messages)));
        }

        public IReadOnlyList<TicketMessageDto> Messages => _selectedTicket?.Messages ?? Array.Empty<TicketMessageDto>();

        public string NewTitle
        {
            get => _newTitle;
            set => SetProperty(ref _newTitle, value);
        }

        public string NewDescription
        {
            get => _newDescription;
            set => SetProperty(ref _newDescription, value);
        }

        public string NewPriority
        {
            get => _newPriority;
            set => SetProperty(ref _newPriority, value);
        }

        public string ReplyBody
        {
            get => _replyBody;
            set => SetProperty(ref _replyBody, value);
        }

        public override async Task OnNavigatedToAsync() => await LoadAsync();

        private async Task LoadAsync()
        {
            IsBusy = true;
            ErrorMessage = null;
            try
            {
                TicketListResponse response = await _services.Api.SearchTicketsAsync(null, null, null, false, 1, 50);
                Tickets.Clear();
                foreach (TicketDto ticket in response.Items)
                {
                    Tickets.Add(ticket);
                }

                StatusMessage = $"{response.TotalCount} ticket(s).";
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task CreateAsync()
        {
            if (string.IsNullOrWhiteSpace(NewTitle) || string.IsNullOrWhiteSpace(NewDescription))
            {
                ErrorMessage = "A subject and a description are required.";
                return;
            }

            try
            {
                TicketDto ticket = await _services.Api.CreateTicketAsync(new CreateTicketRequest(
                    NewTitle.Trim(), NewDescription.Trim(), NewPriority));
                Tickets.Insert(0, ticket);
                NewTitle = string.Empty;
                NewDescription = string.Empty;
                StatusMessage = $"Ticket {ticket.Number} created.";
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
        }

        private async Task ReplyAsync()
        {
            if (SelectedTicket is null || string.IsNullOrWhiteSpace(ReplyBody))
            {
                ErrorMessage = "Select a ticket and write a message.";
                return;
            }

            try
            {
                await _services.Api.AddTicketMessageAsync(SelectedTicket.Ticket.Id, new AddMessageRequest(ReplyBody.Trim()));
                SelectedTicket = await _services.Api.GetTicketAsync(SelectedTicket.Ticket.Id);
                ReplyBody = string.Empty;
                StatusMessage = "Message sent.";
                OnPropertyChanged(nameof(Messages));
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
        }
    }

    public sealed class NotificationsViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private bool _unreadOnly;

        public NotificationsViewModel()
        {
            _services = AppServices.Current;
            Items = new ObservableCollection<NotificationDto>();
            RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
            MarkAllCommand = new AsyncRelayCommand(_ => MarkAllAsync());
            MarkReadCommand = new AsyncRelayCommand(async parameter =>
            {
                if (parameter is NotificationDto notification)
                {
                    try
                    {
                        await _services.Api.MarkNotificationReadAsync(notification.Id);
                        await LoadAsync();
                    }
                    catch (Exception ex)
                    {
                        ErrorMessage = ex.Message;
                    }
                }
            });
        }

        public string this[string key] => Localizer.Instance[key];

        public ObservableCollection<NotificationDto> Items { get; }

        public AsyncRelayCommand RefreshCommand { get; }

        public AsyncRelayCommand MarkAllCommand { get; }

        public AsyncRelayCommand MarkReadCommand { get; }

        public bool UnreadOnly
        {
            get => _unreadOnly;
            set
            {
                if (SetProperty(ref _unreadOnly, value))
                {
                    _ = LoadAsync();
                }
            }
        }

        public override async Task OnNavigatedToAsync() => await LoadAsync();

        private async Task LoadAsync()
        {
            try
            {
                NotificationListResponse response = await _services.Api.GetNotificationsAsync(UnreadOnly, 1, 50);
                Items.Clear();
                foreach (NotificationDto item in response.Items)
                {
                    Items.Add(item);
                }

                StatusMessage = $"{response.UnreadCount} unread.";
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable.";
            }
        }

        private async Task MarkAllAsync()
        {
            try
            {
                int updated = await _services.Api.MarkAllNotificationsReadAsync();
                StatusMessage = $"{updated} notification(s) marked as read.";
                await LoadAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }
    }

    public sealed class AppSettings
    {
        public string ServerUrl { get; set; } = "https://b-vip.onrender.com";

        public string Language { get; set; } = "en";

        public string PlatformToolsPath { get; set; } = string.Empty;
    }

    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private AppSettings _settings;

        public SettingsViewModel()
        {
            _services = AppServices.Current;
            _settings = SettingsStore.Load();
            SaveCommand = new AsyncRelayCommand(_ => SaveAsync(_));
        }

        public string this[string key] => Localizer.Instance[key];

        public bool IsRightToLeft => Localizer.Instance.IsRightToLeft;

        public AsyncRelayCommand SaveCommand { get; }

        /// <summary>
        /// The endpoint is compiled in by <see cref="ApiConfiguration"/>; the screen only shows the
        /// service host so the operator can confirm which cloud the tool is talking to.
        /// </summary>
        public string CloudService => ApiConfiguration.Describe(_services.Api.Options.BaseUrl);

        public string PlatformToolsPath
        {
            get => _settings.PlatformToolsPath;
            set
            {
                if (_settings.PlatformToolsPath != value)
                {
                    _settings.PlatformToolsPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentLanguage => Localizer.Instance.CurrentLanguage;

        public string BackendVersion { get; private set; } = "—";

        public string ServerStatus { get; private set; } = "—";

        public override async Task OnNavigatedToAsync()
        {
            try
            {
                var probe = await _services.Api.ProbeHealthAsync();
                ServerStatus = probe.Healthy ? "Healthy" : $"HTTP {(int)probe.StatusCode}";
                BackendVersion = (await _services.Api.GetMetaAsync()).Version;
            }
            catch (Exception ex)
            {
                ServerStatus = "Unreachable";
                ErrorMessage = ex.Message;
            }

            OnPropertyChanged(nameof(ServerStatus));
            OnPropertyChanged(nameof(BackendVersion));
        }

        private Task SaveAsync(object? _)
        {
            SettingsStore.Save(_settings);
            Modules.ModuleServices.ToolLocatorOptions = new Hardware.ToolLocatorOptions
            {
                PlatformToolsPath = string.IsNullOrWhiteSpace(_settings.PlatformToolsPath) ? null : _settings.PlatformToolsPath
            };
            StatusMessage = "Settings saved. Restart the application to apply the platform-tools path.";
            return Task.CompletedTask;
        }
    }

    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private static string PathFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "B_VIP", "settings.json");

        /// <summary>
        /// Defaults an administrator may ship next to the executable (for example inside the
        /// installer). They are used only until the user saves settings of their own.
        /// </summary>
        private static string DefaultFile => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        public static AppSettings Load()
        {
            foreach (string file in new[] { PathFile, DefaultFile })
            {
                try
                {
                    if (File.Exists(file))
                    {
                        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options) ?? new AppSettings();
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // A broken settings file must never prevent the client from starting.
                }
            }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
                File.WriteAllText(PathFile, JsonSerializer.Serialize(settings, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; failing to persist must not crash the app.
            }
        }
    }

    public sealed class AboutViewModel : ViewModelBase
    {
        public AboutViewModel()
        {
            Version = typeof(AboutViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            var license = AppServices.Current.Session.License;
            LicensedTo = license?.PlanName ?? "—";
        }

        public string this[string key] => Localizer.Instance[key];

        public string Version { get; }

        public string Author => "Bashir Al-Farhan";

        public string LicensedTo { get; }

        public string Product => "B_VIP Tool";

        public string Target => "Windows 10 / 11 · x64";
    }

    public sealed class AdminViewModel : ViewModelBase
    {
        public AdminViewModel()
        {
            Inner = new B_VIP.Admin.AdminViewModel(AppServices.Current.Api);
        }

        public B_VIP.Admin.AdminViewModel Inner { get; }

        public string this[string key] => Localizer.Instance[key];

        public override Task OnNavigatedToAsync() => Inner.InitializeAsync();
    }

    internal static class ReportRedactor
    {
        public static string Redact(string json) => Application.Services.ReportBuilder.Redact(json);
    }
}
