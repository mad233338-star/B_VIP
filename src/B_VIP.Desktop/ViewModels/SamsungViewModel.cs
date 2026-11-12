using System.Collections.ObjectModel;
using System.Windows;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;
using B_VIP.Desktop.Services.Catalog;
using B_VIP.Modules;
using B_VIP.Modules.Samsung;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// Samsung screen: reads the handset's identity (MTP first, adb when it is authorized), sends the
    /// reboot commands, dispatches a browser to the device screen and runs the test-mode FRP routine.
    ///
    /// Every button routes through <see cref="DeviceScreenViewModel.RunOperationAsync"/>, so the
    /// operation is streamed into the live console, the status bar shows progress and the cancel
    /// button can abort it — the UI thread never waits on a modem or a CLI process.
    /// </summary>
    public sealed class SamsungViewModel : DeviceScreenViewModel
    {
        private const int DefaultFrpAuthorizationWaitSeconds = 45;

        public SamsungViewModel()
        {
            Rows = new ObservableCollection<DiagnosticRow>();
            BrowserChoices = [.. BrowserTarget.Presets, BrowserTarget.Custom(string.Empty)];
            _selectedBrowser = BrowserChoices[0];
            _customUrl = "https://accounts.google.com/signin/recovery";

            // The MTP identity read works without an adb device; the rest listens for one and runs
            // the moment it answers, so a button pressed before the cable is plugged in simply waits.
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SamsungReadInfoMtp), _ => !IsBusy);
            RebootDownloadCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SamsungRebootDownload,
                prepare: AdbListener, caption: "Reboot to Download"), _ => !IsBusy);
            RebootSystemCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SamsungRebootSystem,
                prepare: AdbListener, caption: "Reboot to System"), _ => !IsBusy);
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.VendorReboot,
                new Dictionary<string, string> { ["target"] = "recovery" },
                prepare: AdbListener, caption: "Reboot to Recovery"), _ => !IsBusy);
            OpenBrowserCommand = new AsyncRelayCommand(_ => OpenBrowserAsync(), _ => !IsBusy);
            BrowseManualCommand = new AsyncRelayCommand(_ =>
            {
                BrowseManual();
                return Task.CompletedTask;
            });
            ClearManualCommand = new RelayCommand(_ => ManualPath = string.Empty);

            // Both FRP buttons stay clickable with nothing attached: each one listens for its own
            // interface and runs the erasure the moment the handset answers.
            ResetFrpCommand = new AsyncRelayCommand(_ => ResetFrpAsync(), _ => !IsBusy);
            ResetFrpPreloaderCommand = new AsyncRelayCommand(_ => ResetFrpPreloaderAsync(), _ => !IsBusy);

            // Samsung handset catalog: the selected model decides which engine the preloader button drives.
            Catalog = new DeviceCatalogSelectorViewModel(Services.Catalog);
            Catalog.SelectionChanged += OnCatalogModelChanged;

            GateOnBusy(ReadInfoCommand, RebootDownloadCommand, RebootSystemCommand, RebootRecoveryCommand,
                OpenBrowserCommand, ResetFrpCommand, ResetFrpPreloaderCommand);
        }

        /// <summary>The prepare delegate every adb routine uses: press first, plug the cable in after.</summary>
        private Func<CancellationToken, Task<bool>> AdbListener => FrpListener(FrpWaitMode.Adb);

        private string _manualPath = string.Empty;

        /// <summary>
        /// Manual package / loader for this tab. It overrides the bundled download agent the selected
        /// model provides; the engine receives the absolute path, the screen only shows
        /// <see cref="ManualText"/>.
        /// </summary>
        public string ManualPath
        {
            get => _manualPath;
            set
            {
                if (SetProperty(ref _manualPath, value))
                {
                    OnPropertyChanged(nameof(ManualName));
                    OnPropertyChanged(nameof(ManualText));
                    OnPropertyChanged(nameof(ManualTooltip));
                }
            }
        }

        /// <summary>The chosen file's bare name, so no workshop folder appears on screen.</summary>
        public string ManualName => string.IsNullOrEmpty(_manualPath) ? string.Empty : System.IO.Path.GetFileName(_manualPath);

        /// <summary>What the field shows: the bare name, or the auto / bundled default guidance.</summary>
        public string ManualText => string.IsNullOrEmpty(_manualPath)
            ? "Auto / Bundled default (or click Browse to select manual file)..."
            : System.IO.Path.GetFileName(_manualPath);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? ManualTooltip => string.IsNullOrWhiteSpace(_manualPath) ? null : _manualPath;

        public AsyncRelayCommand BrowseManualCommand { get; }

        public RelayCommand ClearManualCommand { get; }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        /// <summary>The browsers offered on the screen; the last entry accepts a custom address.</summary>
        public IReadOnlyList<BrowserTarget> BrowserChoices { get; }

        private BrowserTarget _selectedBrowser;

        public BrowserTarget SelectedBrowser
        {
            get => _selectedBrowser;
            set
            {
                if (SetProperty(ref _selectedBrowser, value))
                {
                    OnPropertyChanged(nameof(CanEditCustomUrl));
                }
            }
        }

        /// <summary>True when the operator picked the custom entry and must type an address.</summary>
        public bool CanEditCustomUrl => string.Equals(_selectedBrowser.Key, "custom", StringComparison.OrdinalIgnoreCase);

        private string _customUrl;

        public string CustomUrl
        {
            get => _customUrl;
            set => SetProperty(ref _customUrl, value);
        }

        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand RebootDownloadCommand { get; }

        public AsyncRelayCommand RebootSystemCommand { get; }

        /// <summary>Reboots into recovery over adb, falling back to fastboot when it refuses.</summary>
        public AsyncRelayCommand RebootRecoveryCommand { get; }

        public AsyncRelayCommand OpenBrowserCommand { get; }

        public AsyncRelayCommand ResetFrpCommand { get; }

        /// <summary>
        /// FRP through the MediaTek preloader (MT6768/MT6765 A-series): mtkclient erases 'frp' and
        /// 'persistent' with Samsung's own download agent and resets the handset. This is the route
        /// for a locked bootloader, where the modem/adb routine below cannot clear anything.
        /// </summary>
        public AsyncRelayCommand ResetFrpPreloaderCommand { get; }

        /// <summary>Brand / model header, scoped to the Samsung catalog.</summary>
        public DeviceCatalogSelectorViewModel Catalog { get; }

        /// <summary>Engine the selected model uses, or "Automatic" when no model is picked.</summary>
        public string SelectedPlatform => _catalogDevice?.Platform ?? "Automatic";

        private CatalogDevice? _catalogDevice;

        public override async Task OnNavigatedToAsync()
        {
            await base.OnNavigatedToAsync();
            await Catalog.LoadAsync();
        }

        private void OnCatalogModelChanged(CatalogDevice? device)
        {
            _catalogDevice = device;
            OnPropertyChanged(nameof(SelectedPlatform));

            if (device is not null)
            {
                Services.Log.Info($"Target model: {device.Summary}; loader: {Catalog.LoaderText}.");
            }
        }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            // The rows are built into a plain list and swapped in on the dispatcher: the MTP/adb read
            // completes on a worker, and clearing a bound DataGrid row by row there is what crashed
            // the table (and its console) the moment "Read Info (MTP)" produced output.
            ApplyRows(Rows, result);
        }

        private Task OpenBrowserAsync()
        {
            BrowserTarget target = CanEditCustomUrl ? BrowserTarget.Custom(CustomUrl.Trim()) : SelectedBrowser;

            if (target.Key == "custom" &&
                (!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                ErrorMessage = "Enter an absolute http:// or https:// address for the custom target.";
                return Task.CompletedTask;
            }

            Dictionary<string, string> parameters = new() { ["target"] = target.Key };
            if (target.Key == "custom")
            {
                parameters["url"] = target.Url;
            }

            return RunOperationAsync(OperationTypes.SamsungOpenBrowser, parameters,
                prepare: AdbListener, caption: "Open on device");
        }

        private void BrowseManual()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select the package / loader for this Samsung handset",
                Filter = "Packages and loaders (*.zip;*.tar;*.md5;*.bin;*.elf;*.mbn;*.pac)|*.zip;*.tar;*.md5;*.bin;*.elf;*.mbn;*.pac|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                ManualPath = dialog.FileName;
            }
        }

        /// <summary>
        /// The boot-level FRP route. The engine follows the selected model: MediaTek handsets go
        /// through mtkclient and Samsung's download agent, Qualcomm units through fh_loader in EDL,
        /// Unisoc units through spd_dump, and Exynos / anything else through a fastboot erase.
        /// </summary>
        private async Task ResetFrpPreloaderAsync()
        {
            string platform = _catalogDevice?.Platform ?? "MediaTek";
            FrpWaitMode wait = platform switch
            {
                "Qualcomm" => FrpWaitMode.QualcommEdl,
                "Spreadtrum" => FrpWaitMode.SpreadtrumBrom,
                "Fastboot" => FrpWaitMode.Fastboot,
                "Exynos" => FrpWaitMode.Fastboot,
                _ => FrpWaitMode.MtkPreloader
            };

            string model = _catalogDevice?.DisplayName ?? "the connected handset";
            MessageBoxResult answer = MessageBox.Show(
                $"Reset FRP at boot level on {model}?\n\n" +
                $"1. Power the handset off.\n" +
                $"2. {FrpInstructions(wait)}\n\n" +
                $"The {platform} engine then erases the protected partitions and resets the phone. " +
                "The partitions are erased permanently. Continue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Preloader routine cancelled.";
                return;
            }

            (string operation, Dictionary<string, string> parameters) = platform switch
            {
                "Qualcomm" => (OperationTypes.QualcommFrpLoader, new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["partitions"] = "frp,config"
                }),
                "Spreadtrum" => (OperationTypes.SpdFrpDump, new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["partition"] = "frp"
                }),
                "Fastboot" => (OperationTypes.FastbootEraseBatch, new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["partitions"] = "frp,config"
                }),
                // Exynos handsets run no MediaTek/Qualcomm engine: they get the same bootloader
                // erase the generic platform does, never an mtkclient handshake they cannot answer.
                "Exynos" => (OperationTypes.FastbootEraseBatch, new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["partitions"] = "frp,config"
                }),
                _ => (OperationTypes.SamsungFrpPreloader, new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["partitions"] = "frp,persistent"
                })
            };

            // A catalog loader overrides the engine's default download agent / programmer, and a
            // file chosen by hand overrides the catalog.
            if (LoaderKeyFor(platform) is { } key)
            {
                if (Catalog.LoaderPath is { } loader)
                {
                    parameters[key] = loader;
                }

                if (!string.IsNullOrWhiteSpace(ManualPath))
                {
                    parameters[key] = ManualPath.Trim();
                    Services.Log.Info($"Manual override: {System.IO.Path.GetFileName(ManualPath)}.");
                }
            }

            OnUiThread(() => Rows.Clear());
            await RunOperationAsync(operation, parameters, confirmDataLoss: true,
                prepare: FrpListener(wait), caption: $"Reset FRP ({wait})");
        }

        /// <summary>The parameter each boot-level engine reads its loader from; null when it takes none.</summary>
        private static string? LoaderKeyFor(string platform) => platform switch
        {
            "MediaTek" => "daFile",
            "Qualcomm" => "firehosePath",
            "Spreadtrum" => "fdl1Path",
            _ => null
        };

        /// <summary>FRP through the Samsung modem / ADB test-mode sequence.</summary>
        private async Task ResetFrpAsync()
        {
            MessageBoxResult answer = MessageBox.Show(
                "The FRP routine clears the setup-complete flags on the connected Samsung handset and reboots it.\n\n" +
                "It needs an authorized adb connection: the modem sequence cannot approve the USB debugging prompt by itself.\n\n" +
                "Continue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "FRP routine cancelled.";
                return;
            }

            await RunOperationAsync(OperationTypes.SamsungFrpTestMode, new Dictionary<string, string>
            {
                ["confirm"] = "yes",
                ["waitSeconds"] = DefaultFrpAuthorizationWaitSeconds.ToString()
            }, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.Adb),
                caption: "Reset FRP (ADB)");
        }
    }
}
