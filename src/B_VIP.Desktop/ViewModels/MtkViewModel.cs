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

            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.MtkReadInfo));
            HandshakeCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.MtkHandshake));
            RebootCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.MtkReboot,
                new Dictionary<string, string> { ["target"] = SelectedRebootTarget }));
            ResetFrpCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Erase the FRP partition through fastboot?\n\nThe handset is rebooted into the bootloader and 'frp' plus 'persistent' are erased. " +
                "This needs an unlocked bootloader and cannot be undone.",
                () => RunOperationAsync(OperationTypes.MtkResetFrp,
                    new Dictionary<string, string> { ["confirm"] = "yes" }, confirmDataLoss: true)));

            // The boot-ROM route: works on a locked bootloader because the erase happens before
            // Android starts. mtkclient is driven from the loader bundle (Loaders\MTK).
            ResetFrpClientCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Reset FRP through the MediaTek boot ROM?\n\n" +
                "1. Power the handset off.\n" +
                "2. Hold Vol+ (and Vol-) while you connect the USB cable.\n\n" +
                "mtkclient erases 'frp' and 'persistent' and resets the phone. " +
                "This works on a locked bootloader and cannot be undone.",
                () => RunOperationAsync(OperationTypes.MtkFrpClient, new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["partitions"] = "frp,persistent"
                }, confirmDataLoss: true)));
            FactoryResetCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Format the handset?\n\nuserdata, cache and metadata are erased; nvram, nvdata, protect1/2 and modem are left untouched. " +
                "Every file on the phone is lost.",
                () => RunOperationAsync(OperationTypes.MtkFactoryReset,
                    new Dictionary<string, string> { ["confirm"] = "yes" }, confirmDataLoss: true)));
            ScatterCommand = new AsyncRelayCommand(_ => RunScatterAsync());
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
        }

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

        public AsyncRelayCommand ResetFrpCommand { get; }

        /// <summary>FRP through mtkclient and the boot ROM, for a handset with a locked bootloader.</summary>
        public AsyncRelayCommand ResetFrpClientCommand { get; }

        public AsyncRelayCommand FactoryResetCommand { get; }

        public AsyncRelayCommand ScatterCommand { get; }

        public AsyncRelayCommand BrowseScatterCommand { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

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
                new Dictionary<string, string> { ["scatterPath"] = ScatterPath.Trim() });
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
