using System.Windows;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Services;
using B_VIP.Desktop.ViewModels;
using B_VIP.Desktop.Views;

namespace B_VIP.Desktop
{
    public partial class App : System.Windows.Application
    {
        /// <summary>Distinct failures already shown; layout faults repeat on every frame.</summary>
        private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);

        private const int MaxFailureDialogs = 3;

        /// <summary>1 while a failure dialog is open, so a fault raised inside it cannot nest.</summary>
        private static int _reporting;

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

            // A fault is logged and reported once. The handler never re-enters: an exception raised
            // while the dialog is open, or the same fault repeating every layout pass, used to stack
            // modal windows until the process died.
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                ReportFailure(args.Exception);
            };

            // Work that faults off the UI thread (a module's background continuation) never reaches the
            // dispatcher; recording it keeps the console honest about what stopped.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception)
                {
                    LogFailure(exception);
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogFailure(args.Exception);
                args.SetObserved();
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

        /// <summary>
        /// Logs a failure and shows at most one dialog for it, at most one at a time. The exception is
        /// always treated as handled, so a repeating layout fault cannot walk the dispatcher.
        /// </summary>
        private static void ReportFailure(Exception exception)
        {
            Exception root = Unwrap(exception);
            LogFailure(root);

            string key = $"{root.GetType().Name}: {root.Message}";
            if (ReportedFailures.Contains(key) || ReportedFailures.Count >= MaxFailureDialogs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _reporting, 1, 0) != 0)
            {
                return;
            }

            try
            {
                ReportedFailures.Add(key);
                MessageBox.Show(root.Message, "B_VIP Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // Reporting a crash must never crash the reporter.
            }
            finally
            {
                Interlocked.Exchange(ref _reporting, 0);
            }
        }

        private static void LogFailure(Exception exception)
        {
            try
            {
                AppServices? services = AppServices.TryGetCurrent();
                services?.Log.Error($"Unhandled {exception.GetType().Name}: {exception.Message}");
            }
            catch (Exception)
            {
                // The console may already be gone during shutdown.
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            Exception current = exception;
            while (current is AggregateException { InnerExceptions.Count: 1 } aggregate)
            {
                current = aggregate.InnerExceptions[0];
            }

            return current;
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
