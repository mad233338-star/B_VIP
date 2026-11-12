using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;
using B_VIP.Desktop.Services.Loaders;
using B_VIP.Hardware;
using B_VIP.Modules;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// MediaTek screen: lists the BROM / preloader / VCOM interfaces, probes them, and drives the
    /// partition routines the engine really implements. Everything runs through
    /// <see cref="DeviceScreenViewModel.RunOperationAsync"/>, so output lands in the live console and
    /// the UI thread never waits on a port or a CLI process.
    /// </summary>
    public sealed class MtkViewModel : DeviceScreenViewModel
    {
        private readonly MtkLoaderManager _loaders;

        public MtkViewModel()
        {
            // The mtkclient bundle lives in Loaders\MTK and is provisioned on first visit.
            _loaders = new MtkLoaderManager(Services.Loaders);
            Loaders = new LoaderPanelViewModel(_loaders.Loaders, "MediaTek loader package");

            Rows = new ObservableCollection<DiagnosticRow>();
            Ports = new ObservableCollection<MtkPort>();
            RebootTargets = ["system", "fastboot", "recovery"];
            _selectedRebootTarget = RebootTargets[0];
            _scatterPath = string.Empty;

            RefreshPorts();

            // Every operation button is clickable with no handset attached: it listens for the
            // preloader / BROM interface and runs the moment one answers.
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.MtkReadInfo,
                prepare: MtkListener, caption: "Read info (MediaTek)"), _ => !IsBusy);
            HandshakeCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.MtkHandshake,
                prepare: MtkListener, caption: "BROM handshake"), _ => !IsBusy);
            RebootCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.MtkReboot,
                new Dictionary<string, string> { ["target"] = SelectedRebootTarget },
                prepare: MtkListener, caption: $"Reboot to {SelectedRebootTarget}"), _ => !IsBusy);
            // The single Reset FRP action: the boot ROM is the only route that works on a locked
            // bootloader, so mtkclient is driven from the loader bundle (Loaders\MTK). The command is
            // always clickable; it listens for the preloader and runs the moment the handset answers.
            ResetFrpCommand = new AsyncRelayCommand(_ => ResetFrpAsync(), _ => !IsBusy);
            FactoryResetCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Format the handset?\n\nuserdata, cache and metadata are erased; nvram, nvdata, protect1/2 and modem are left untouched. " +
                "Every file on the phone is lost.",
                () => RunOperationAsync(OperationTypes.MtkFactoryReset,
                    new Dictionary<string, string> { ["confirm"] = "yes" }, confirmDataLoss: true,
                    prepare: MtkListener, caption: "Factory format (BROM)")), _ => !IsBusy);
            ScatterCommand = new AsyncRelayCommand(_ => RunScatterAsync(), _ => !IsBusy);
            BrowseScatterCommand = new AsyncRelayCommand(_ =>
            {
                BrowseScatter();
                return Task.CompletedTask;
            });
            RefreshPortsCommand = new AsyncRelayCommand(_ =>
            {
                StatusMessage = $"{RefreshPorts()} MediaTek interface(s).";
                return Task.CompletedTask;
            });

            // The rest of the boot-ROM workbench. Every one of these listens for the preloader first,
            // so the operator can press the button before plugging the handset in.
            UnlockBootloaderCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Unlock the bootloader through the boot ROM?\n\n" +
                "mtkclient erases 'seccfg', the partition that stores the lock state. The handset wipes itself on the " +
                "first boot afterwards, so this is never done on a customer's phone without a backup. Continue?",
                () => RunOperationAsync(OperationTypes.MtkUnlockBootloader,
                    new Dictionary<string, string> { ["confirm"] = "yes" }, confirmDataLoss: true,
                    prepare: MtkListener, caption: "Unlock bootloader")), _ => !IsBusy);
            RelockBootloaderCommand = new AsyncRelayCommand(_ => RelockBootloaderAsync(), _ => !IsBusy);
            BackupNvramCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.MtkBackupNvram,
                prepare: MtkListener, caption: "Backup NVRAM"), _ => !IsBusy);
            RestoreNvramCommand = new AsyncRelayCommand(_ => RestoreNvramAsync(), _ => !IsBusy);
            SafeFormatCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Safe format of userdata?\n\n" +
                "Only 'userdata' is erased. metadata, persistent flags and the FRP state stay in place, which is what " +
                "breaks the encryption loop without clearing the account lock. Every file on the phone is lost.",
                () => RunOperationAsync(OperationTypes.MtkSafeFormat,
                    new Dictionary<string, string> { ["confirm"] = "yes" }, confirmDataLoss: true,
                    prepare: MtkListener, caption: "Safe format")), _ => !IsBusy);

            GateOnBusy(ReadInfoCommand, HandshakeCommand, RebootCommand, ResetFrpCommand, FactoryResetCommand,
                ScatterCommand, UnlockBootloaderCommand, RelockBootloaderCommand, BackupNvramCommand,
                RestoreNvramCommand, SafeFormatCommand);
        }

        /// <summary>The prepare delegate every boot-ROM routine uses: press first, plug the cable in after.</summary>
        private Func<CancellationToken, Task<bool>> MtkListener => FrpListener(FrpWaitMode.MtkPreloader);

        public ObservableCollection<DiagnosticRow> Rows { get; }

        /// <summary>Download state of the MediaTek loader bundle (Loaders\MTK).</summary>
        public LoaderPanelViewModel Loaders { get; }

        /// <summary>Live list of MediaTek USB/COM interfaces found on this computer.</summary>
        public ObservableCollection<MtkPort> Ports { get; }

        public IReadOnlyList<string> RebootTargets { get; }

        private string _selectedRebootTarget;

        public string SelectedRebootTarget
        {
            get => _selectedRebootTarget;
            set => SetProperty(ref _selectedRebootTarget, value);
        }

        private string _scatterPath;

        /// <summary>
        /// Path of the scatter XML/TXT to parse (and optionally flash from). The engine gets this
        /// absolute path; the screen shows only <see cref="ScatterName"/>.
        /// </summary>
        public string ScatterPath
        {
            get => _scatterPath;
            set
            {
                if (SetProperty(ref _scatterPath, value))
                {
                    OnPropertyChanged(nameof(ScatterName));
                    OnPropertyChanged(nameof(ScatterTooltip));
                }
            }
        }

        /// <summary>The scatter file's bare name, so no workshop folder appears on screen.</summary>
        public string ScatterName => string.IsNullOrEmpty(_scatterPath) ? string.Empty : Path.GetFileName(_scatterPath);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? ScatterTooltip => string.IsNullOrWhiteSpace(_scatterPath) ? null : _scatterPath;

        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand HandshakeCommand { get; }

        public AsyncRelayCommand RebootCommand { get; }

        /// <summary>FRP through mtkclient and the boot ROM, for a handset with a locked bootloader.</summary>
        public AsyncRelayCommand ResetFrpCommand { get; }

        public AsyncRelayCommand FactoryResetCommand { get; }

        public AsyncRelayCommand ScatterCommand { get; }

        public AsyncRelayCommand BrowseScatterCommand { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

        /// <summary>Erases 'seccfg' through the boot ROM: the one-click unlock.</summary>
        public AsyncRelayCommand UnlockBootloaderCommand { get; }

        /// <summary>Writes a previously dumped 'seccfg' back, restoring the locked state.</summary>
        public AsyncRelayCommand RelockBootloaderCommand { get; }

        /// <summary>Dumps nvram, nvdata, nvcfg, protect1 and protect2 into Backups\NV.</summary>
        public AsyncRelayCommand BackupNvramCommand { get; }

        /// <summary>Restores those dumps from a folder chosen by the operator.</summary>
        public AsyncRelayCommand RestoreNvramCommand { get; }

        /// <summary>Formats userdata only, leaving metadata and the FRP flags intact.</summary>
        public AsyncRelayCommand SafeFormatCommand { get; }

        /// <summary>
        /// Opening the screen also makes sure the loader bundle is on disk. An existing copy is reused
        /// without touching the network, which keeps an offline workshop running.
        /// </summary>
        public override async Task OnNavigatedToAsync()
        {
            await base.OnNavigatedToAsync();
            await Loaders.EnsureReadyAsync();
        }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            ApplyRows(Rows, result);
            RefreshPorts();
        }

        private int RefreshPorts()
        {
            // The list is collected first and handed to the dispatcher in one pass, so the bound
            // collection is never cleared and refilled while the screen is rendering.
            List<MtkPort> ports = new();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    ports.AddRange(MtkPortLocator.FindPorts());
                }
                catch (Exception ex)
                {
                    Services.Log.Warn($"MediaTek port enumeration failed: {ex.Message}");
                }
            }

            OnUiThread(() =>
            {
                Ports.Clear();
                foreach (MtkPort port in ports)
                {
                    Ports.Add(port);
                }
            });

            return Ports.Count;
        }

        private void BrowseScatter()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select a MediaTek scatter file",
                Filter = "Scatter tables (*.xml;*.txt)|*.xml;*.txt|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                ScatterPath = dialog.FileName;
            }
        }

        private Task RunScatterAsync()
        {
            if (string.IsNullOrWhiteSpace(ScatterPath))
            {
                ErrorMessage = "Choose a scatter file first.";
                return Task.CompletedTask;
            }

            return RunOperationAsync(OperationTypes.MtkScatter,
                new Dictionary<string, string> { ["scatterPath"] = ScatterPath.Trim() },
                prepare: MtkListener, caption: "Scatter flash");
        }

        private async Task ResetFrpAsync()
        {
            MessageBoxResult answer = MessageBox.Show(
                "Reset FRP through the MediaTek boot ROM?\n\n" +
                "1. Power the handset off.\n" +
                "2. Hold Vol+ (and Vol-) while you connect the USB cable.\n\n" +
                "mtkclient erases 'frp' and 'persistent' and resets the phone. " +
                "This works on a locked bootloader and cannot be undone.",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            OnUiThread(() => Rows.Clear());
            await RunOperationAsync(OperationTypes.MtkFrpClient, new Dictionary<string, string>
            {
                ["confirm"] = "yes",
                ["partitions"] = "frp,persistent"
            }, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.MtkPreloader),
                caption: "Reset FRP (BROM)");
        }

        private async Task RelockBootloaderAsync()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select the seccfg backup for this handset",
                Filter = "seccfg image (*.bin)|*.bin|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "Relock cancelled: a valid seccfg backup is required.";
                return;
            }

            Dictionary<string, string> parameters = new()
            {
                ["confirm"] = "yes",
                ["seccfgPath"] = dialog.FileName
            };

            await RunOperationAsync(OperationTypes.MtkRelockBootloader, parameters, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.MtkPreloader), caption: "Relock bootloader");
        }

        private async Task RestoreNvramAsync()
        {
            Microsoft.Win32.OpenFolderDialog dialog = new()
            {
                Title = "Select the NVRAM backup folder (nvram.bin, nvdata.bin, ...)",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                StatusMessage = "Restore cancelled: no backup folder was chosen.";
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "Restore NVRAM / NVDATA from this backup?\n\n" +
                "The dumps carry this handset's radio calibration and IMEI. Writing them into another phone corrupts its " +
                "identity; writing the wrong set into this one can break the modem. Continue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Restore cancelled.";
                return;
            }

            Dictionary<string, string> parameters = new()
            {
                ["confirm"] = "yes",
                ["backupPath"] = dialog.FolderName
            };

            await RunOperationAsync(OperationTypes.MtkRestoreNvram, parameters, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.MtkPreloader), caption: "Restore NVRAM");
        }

        private async Task ConfirmedAsync(string question, Func<Task> action)
        {
            MessageBoxResult answer = MessageBox.Show(question, "B_VIP Tool", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            await action();
        }
    }
}
