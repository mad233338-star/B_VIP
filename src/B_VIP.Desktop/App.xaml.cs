using System.Windows;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Services;
using B_VIP.Desktop.ViewModels;
using B_VIP.Desktop.Views;

namespace B_VIP.Desktop
{
    public partial class App : System.Windows.Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppSettings settings = SettingsStore.Load();
            // English-only build: the culture is locked regardless of the saved preference.
            Localizer.Instance.SetLanguage(Localizer.English);

            // Platform tools are resolved automatically: the bundled platform-tools folder next to
            // the executable first, then PATH and the development .tools folders. There is no
            // operator-chosen folder any more.
            Hardware.ToolLocatorOptions toolOptions = new();
            Modules.ModuleServices.ToolLocatorOptions = toolOptions;

            // The cloud endpoint is compiled in (ApiConfiguration); only a development override may
            // change it, and the log never prints it.
            string baseUrl = ApiConfiguration.ResolveBaseUrl(settings.ServerUrl);
            AppServices services = AppServices.Initialize(baseUrl, toolOptions);

            string version = typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            services.Log.Info($"B_VIP Tool {version} starting — Cloud Service: Connected");
            ReportToolDiscovery(services);
            services.Monitor.Start();

            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(args.Exception.Message, "B_VIP Tool",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            LoginWindow login = new();

            login.ViewModel.SignedIn += _ =>
            {
                AppServices current = AppServices.Current;

                // Nothing navigates on a failed or incomplete sign-in: the event is only raised
                // after a verified session, and this guard enforces the same rule again.
                if (!current.Session.IsSignedIn || current.Session.User is null)
                {
                    return;
                }

                // One binary, two roles, no shared shell: an administrator gets the management
                // console, a technician the device workspace. The technician shell is never
                // constructed for an administrator and neither window links to the other.
                if (current.Session.IsAdmin)
                {
                    AdminDashboardWindow admin = new();
                    MainWindow = admin;
                    admin.Show();
                    login.Close();
                    return;
                }

                services.License.Start();

                MainWindow shell = new();
                shell.DataContext = new MainViewModel();
                MainWindow = shell;
                shell.Show();
                login.Close();
            };

            MainWindow = login;
            login.Show();

            await login.ViewModel.InitializeAsync();
        }

        /// <summary>Publishes the real adb/fastboot resolution to the console, not a simulated status.</summary>
        private static void ReportToolDiscovery(AppServices services)
        {
            string? adb = services.Tools.Find(Hardware.ToolLocator.Adb);
            string? fastboot = services.Tools.Find(Hardware.ToolLocator.Fastboot);
            int found = (adb is null ? 0 : 1) + (fastboot is null ? 0 : 1);

            if (found == 2)
            {
                services.Log.Success($"External tools: 2/2 — adb={adb}, fastboot={fastboot}");
                return;
            }

            services.Log.Warn($"External tools: {found}/2 — adb={(adb ?? "not found")}, fastboot={(fastboot ?? "not found")}");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppServices? services = AppServices.TryGetCurrent();
            services?.Log.Info("B_VIP Tool shutting down.");
            services?.Shutdown();
            base.OnExit(e);
        }
    }
}
