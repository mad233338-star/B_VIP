using System.IO;
using System.Net.Http;
using System.Text.Json;
using B_VIP.CloudClient;
using B_VIP.Contracts.Auth;
using B_VIP.Desktop.Mvvm;
using B_VIP.Hardware;
using B_VIP.Modules;

namespace B_VIP.Desktop.Services
{
    /// <summary>Persists the session in the user profile so a restart keeps the technician signed in.</summary>
    public sealed class FileTokenStore : ITokenStore
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private readonly string _path;

        public FileTokenStore(string? path = null)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "B_VIP");
            Directory.CreateDirectory(folder);
            _path = path ?? Path.Combine(folder, "session.json");
        }

        public async Task<AuthTokenDto?> LoadAsync(CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                string json = await File.ReadAllTextAsync(_path, ct);
                return JsonSerializer.Deserialize<AuthTokenDto>(json, Options);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public async Task SaveAsync(AuthTokenDto tokens, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(tokens, Options);
            await File.WriteAllTextAsync(_path, json, ct);
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (IOException)
            {
                // A locked session file must not block signing out.
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>The signed-in state shared by every view model.</summary>
    public sealed class SessionService : ObservableObject
    {
        private UserSummaryDto? _user;
        private IReadOnlyList<string> _permissions = Array.Empty<string>();
        private LicenseSummaryDto? _license;
        private bool _isSignedIn;

        public UserSummaryDto? User
        {
            get => _user;
            set => SetProperty(ref _user, value, () =>
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(IsAdmin));
            });
        }

        public IReadOnlyList<string> Permissions
        {
            get => _permissions;
            set => SetProperty(ref _permissions, value, () => OnPropertyChanged(nameof(IsAdmin)));
        }

        public LicenseSummaryDto? License
        {
            get => _license;
            set => SetProperty(ref _license, value);
        }

        public bool IsSignedIn
        {
            get => _isSignedIn;
            set => SetProperty(ref _isSignedIn, value);
        }

        /// <summary>The one account that always owns the management console.</summary>
        public const string MasterAdminEmail = "bviptool@gmail.com";

        public string DisplayName => _user?.DisplayName ?? _user?.UserName ?? string.Empty;

        /// <summary>
        /// Administrator detection is role first (the login response carries the account's roles),
        /// with the master administrator's address as the fallback. It is deliberately narrow: a
        /// technician permission such as <c>users.write</c> must never open the admin console.
        /// </summary>
        public bool IsAdmin =>
            (_user?.Roles.Any(IsAdminRole) ?? false) ||
            string.Equals(_user?.Email, MasterAdminEmail, StringComparison.OrdinalIgnoreCase);

        private static bool IsAdminRole(string? role) =>
            !string.IsNullOrWhiteSpace(role) && role.Contains("admin", StringComparison.OrdinalIgnoreCase);

        public bool Has(string permission) => _permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

        public void Clear()
        {
            User = null;
            Permissions = Array.Empty<string>();
            License = null;
            IsSignedIn = false;
        }
    }

    /// <summary>Composition root for the desktop application.</summary>
    public sealed class AppServices
    {
        private static AppServices? _current;

        public static AppServices Current => _current ?? throw new InvalidOperationException("The desktop services were not initialised.");

        /// <summary>Null before startup and after the application has been torn down.</summary>
        public static AppServices? TryGetCurrent() => _current;

        public static AppServices Initialize(string baseUrl, ToolLocatorOptions? toolOptions = null)
        {
            HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };
            ApiClientOptions options = new()
            {
                BaseUrl = baseUrl,
                ClientVersion = typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "1.0.0"
            };

            ITokenStore tokens = new FileTokenStore();
            BVipApiClient api = new(http, options, tokens);

            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            ConsoleLoggerService log = new(dispatcher);

            _current = new AppServices(api, tokens, new SessionService(), new NavigationService(),
                toolOptions ?? new ToolLocatorOptions(), log, dispatcher);
            return _current;
        }

        private AppServices(
            BVipApiClient api,
            ITokenStore tokens,
            SessionService session,
            NavigationService navigation,
            ToolLocatorOptions toolOptions,
            ConsoleLoggerService log,
            System.Windows.Threading.Dispatcher? dispatcher)
        {
            Api = api;
            Tokens = tokens;
            Session = session;
            Navigation = navigation;
            Log = log;

            // One engine for every external tool: it streams the command line, output and exit
            // status to the console with colour tags and honours the caller's cancellation token.
            Cli = new CliProcessService(new ProcessRunner(), log);
            ProcessRunner = Cli;
            Tools = new ToolLocator(toolOptions, ProcessRunner);
            Adb = new AdbClient(Tools, ProcessRunner);
            Detector = new DeviceDetector(Adb, Tools);
            Diagnostics = new DeviceDiagnostics(Adb);
            Registry = ModuleServices.CreateRegistry(Adb, Tools, ProcessRunner);
            Devices = new DeviceWorkspace(Detector, Diagnostics, Tools, Registry, Api, Session, Log);
            Ports = new PortWatcherService();
            Monitor = new DeviceMonitorService(Ports, Detector, Log, dispatcher);
            Progress = new ViewModels.OperationProgressViewModel(Log);
            License = new LicenseMonitorService(api, session, log, dispatcher);
        }

        public BVipApiClient Api { get; }

        public ITokenStore Tokens { get; }

        /// <summary>Application-wide console sink (channel based, UI-thread batched).</summary>
        public ConsoleLoggerService Log { get; }

        /// <summary>Windows USB/COM watcher; started by the shell.</summary>
        public PortWatcherService Ports { get; }

        /// <summary>Live view of ports plus adb/fastboot devices for the status bar.</summary>
        public DeviceMonitorService Monitor { get; }

        /// <summary>
        /// The single task dispatcher behind the status-bar progress bar: any screen starts long
        /// work through it so the operator sees progress and can cancel from one place.
        /// </summary>
        public ViewModels.OperationProgressViewModel Progress { get; }

        public SessionService Session { get; }

        public NavigationService Navigation { get; }

        public IProcessRunner ProcessRunner { get; }

        /// <summary>The centralized adb/fastboot runner behind every device operation.</summary>
        public CliProcessService Cli { get; }

        public IToolLocator Tools { get; }

        public IAdbClient Adb { get; }

        public IDeviceDetector Detector { get; }

        public IDeviceDiagnostics Diagnostics { get; }

        public OperationExecutorRegistry Registry { get; }

        public DeviceWorkspace Devices { get; }

        /// <summary>Startup + one-minute heartbeat that locks the workspace when the plan lapses.</summary>
        public LicenseMonitorService License { get; }

        /// <summary>Stops the watchers and flushes the console before the process goes away.</summary>
        public void Shutdown()
        {
            try
            {
                Monitor.Dispose();
                License.Dispose();
            }
            finally
            {
                Log.Dispose();
            }
        }
    }
}
