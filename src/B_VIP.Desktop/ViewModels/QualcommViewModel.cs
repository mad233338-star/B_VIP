using System.Collections.ObjectModel;
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
    /// Qualcomm screen: lists the 9008 interfaces, takes the programmer binary for the connected SoC,
    /// and drives the Sahara / Firehose routines the engine really implements. Every routine runs
    /// through <see cref="DeviceScreenViewModel.RunOperationAsync"/>, so its steps stream to the live
    /// console and the shell's progress bar carries the cancel action.
    /// </summary>
    public sealed class QualcommViewModel : DeviceScreenViewModel
    {
        /// <summary>Stand-in entry that keeps the programmer on the port the loader was opened on.</summary>
        public const string SamePortOption = "(same port as the loader)";

        private readonly QualcommLoaderManager _loaders;

        public QualcommViewModel()
        {
            // The programmer bundle lives in Loaders\Qualcomm and is provisioned on first visit.
            _loaders = new QualcommLoaderManager(Services.Loaders);
            Loaders = new LoaderPanelViewModel(_loaders.Loaders, "Qualcomm loader package");

            Rows = new ObservableCollection<DiagnosticRow>();
            Ports = new ObservableCollection<QualcommPort>();
            PowerTargets = ["reset", "off", "edl"];
            PortChoices = new ObservableCollection<string>();

            _selectedPowerTarget = PowerTargets[0];
            _selectedFirehosePort = SamePortOption;
            _firehosePath = string.Empty;

            RefreshPorts();

            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.QualcommReadInfo));
            SaharaCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.QualcommSahara, BuildParameters()));
            GptCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.QualcommGpt, BuildParameters()));
            ResetFrpCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Erase the FRP partition through EDL?\n\n" +
                "The partition table is read first, then exactly the 'frp' (or 'config') partition range is erased. " +
                "This cannot be undone.",
                () => RunOperationAsync(OperationTypes.QualcommResetFrp,
                    WithConfirmation(BuildParameters()), confirmDataLoss: true)));
            FactoryResetCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Format the handset through EDL?\n\n" +
                "userdata and cache are erased; modemst1, modemst2, fsg and persist are never touched. " +
                "Every file on the phone is lost.",
                () => RunOperationAsync(OperationTypes.QualcommFactoryReset,
                    WithConfirmation(BuildParameters()), confirmDataLoss: true)));
            RebootCommand = new AsyncRelayCommand(_ =>
            {
                Dictionary<string, string> parameters = BuildParameters();
                parameters["target"] = SelectedPowerTarget;
                return RunOperationAsync(OperationTypes.QualcommReboot, parameters);
            });
            BrowseFirehoseCommand = new AsyncRelayCommand(_ =>
            {
                BrowseFirehose();
                return Task.CompletedTask;
            });
            RefreshPortsCommand = new AsyncRelayCommand(_ =>
            {
                StatusMessage = $"{RefreshPorts()} Qualcomm interface(s).";
                return Task.CompletedTask;
            });
        }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        /// <summary>Download state of the Qualcomm loader bundle (Loaders\Qualcomm).</summary>
        public LoaderPanelViewModel Loaders { get; }

        /// <summary>Live list of Qualcomm USB/COM interfaces found on this computer.</summary>
        public ObservableCollection<QualcommPort> Ports { get; }

        /// <summary>Ports offered for the Firehose stage after the programmer has started.</summary>
        public ObservableCollection<string> PortChoices { get; }

        public IReadOnlyList<string> PowerTargets { get; }

        private string _firehosePath;

        /// <summary>Programmer binary (prog_firehose_*.elf / .mbn) for the connected SoC.</summary>
        public string FirehosePath
        {
            get => _firehosePath;
            set => SetProperty(ref _firehosePath, value);
        }

        private string _selectedFirehosePort;

        /// <summary>Where Firehose should reconnect once the programmer is running.</summary>
        public string SelectedFirehosePort
        {
            get => _selectedFirehosePort;
            set => SetProperty(ref _selectedFirehosePort, value);
        }

        private string _selectedPowerTarget;

        public string SelectedPowerTarget
        {
            get => _selectedPowerTarget;
            set => SetProperty(ref _selectedPowerTarget, value);
        }

        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand SaharaCommand { get; }

        public AsyncRelayCommand GptCommand { get; }

        public AsyncRelayCommand ResetFrpCommand { get; }

        public AsyncRelayCommand FactoryResetCommand { get; }

        public AsyncRelayCommand RebootCommand { get; }

        public AsyncRelayCommand BrowseFirehoseCommand { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

        /// <summary>
        /// Opening the screen also makes sure the loader bundle is on disk. A workshop computer that
        /// is offline keeps the copy it already has, so this is a folder check in the normal case.
        /// </summary>
        public override async Task OnNavigatedToAsync()
        {
            await base.OnNavigatedToAsync();
            await Loaders.EnsureReadyAsync();

            if (Loaders.IsReady && string.IsNullOrWhiteSpace(FirehosePath))
            {
                FirehosePath = _loaders.FindProgrammers().FirstOrDefault() ?? string.Empty;
            }
        }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            ApplyRows(Rows, result);
            RefreshPorts();
        }

        /// <summary>Reads the ports once on construction and again after every routine.</summary>
        private int RefreshPorts()
        {
            // Collected first, then handed to the dispatcher in one pass: the bound list is never
            // cleared and refilled from a worker while the screen is rendering.
            List<QualcommPort> ports = new();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    ports.AddRange(QualcommPortLocator.FindPorts());
                }
                catch (Exception ex)
                {
                    Services.Log.Warn($"Qualcomm port enumeration failed: {ex.Message}");
                }
            }

            OnUiThread(() =>
            {
                Ports.Clear();
                PortChoices.Clear();
                PortChoices.Add(SamePortOption);

                foreach (QualcommPort port in ports)
                {
                    Ports.Add(port);
                    if (!string.IsNullOrEmpty(port.PortName) &&
                        port.PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        PortChoices.Add(port.PortName);
                    }
                }

                if (!PortChoices.Contains(SelectedFirehosePort))
                {
                    SelectedFirehosePort = SamePortOption;
                }
            });

            return Ports.Count;
        }

        private Dictionary<string, string> BuildParameters()
        {
            Dictionary<string, string> parameters = new();
            if (!string.IsNullOrWhiteSpace(FirehosePath))
            {
                parameters["firehosePath"] = FirehosePath.Trim();
            }

            if (!string.IsNullOrWhiteSpace(SelectedFirehosePort) &&
                !string.Equals(SelectedFirehosePort, SamePortOption, StringComparison.Ordinal))
            {
                parameters["firehosePort"] = SelectedFirehosePort;
            }

            return parameters;
        }

        private static Dictionary<string, string> WithConfirmation(Dictionary<string, string> parameters)
        {
            parameters["confirm"] = "yes";
            return parameters;
        }

        private void BrowseFirehose()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select the Qualcomm programmer (Firehose loader)",
                Filter = "Programmers (*.elf;*.mbn;*.bin)|*.elf;*.mbn;*.bin|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                FirehosePath = dialog.FileName;
            }
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
