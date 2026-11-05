using System.Collections.ObjectModel;
using System.Windows;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;
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
        public MtkViewModel()
        {
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

        /// <summary>Path of the scatter XML/TXT to parse (and optionally flash from).</summary>
        public string ScatterPath
        {
            get => _scatterPath;
            set => SetProperty(ref _scatterPath, value);
        }

        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand HandshakeCommand { get; }

        public AsyncRelayCommand RebootCommand { get; }

        public AsyncRelayCommand ResetFrpCommand { get; }

        public AsyncRelayCommand FactoryResetCommand { get; }

        public AsyncRelayCommand ScatterCommand { get; }

        public AsyncRelayCommand BrowseScatterCommand { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            Rows.Clear();
            foreach (DiagnosticCheckDto check in result.Checks)
            {
                Rows.Add(new DiagnosticRow { Name = check.Name, Status = check.Status, Detail = check.Detail });
            }

            foreach (string line in result.Log)
            {
                Rows.Add(new DiagnosticRow { Name = "log", Status = "info", Detail = line });
            }

            foreach (string warning in result.Warnings)
            {
                Rows.Add(new DiagnosticRow { Name = "notice", Status = "warn", Detail = warning });
            }

            RefreshPorts();
        }

        private int RefreshPorts()
        {
            Ports.Clear();
            if (!OperatingSystem.IsWindows())
            {
                return 0;
            }

            try
            {
                foreach (MtkPort port in MtkPortLocator.FindPorts())
                {
                    Ports.Add(port);
                }
            }
            catch (Exception ex)
            {
                Services.Log.Warn($"MediaTek port enumeration failed: {ex.Message}");
            }

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
