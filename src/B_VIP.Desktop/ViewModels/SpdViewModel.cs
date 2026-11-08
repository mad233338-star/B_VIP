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
    /// Unisoc screen: lists the SPRD boot-ROM / diag interfaces, takes the FDL loader images that
    /// match the SoC, and drives the routines the engine implements. Every routine runs through
    /// <see cref="DeviceScreenViewModel.RunOperationAsync"/>, so the steps stream into the live
    /// console and the shell's progress bar carries the cancel action.
    /// </summary>
    public sealed class SpdViewModel : DeviceScreenViewModel
    {
        /// <summary>Keeps the routine on whichever SPD port the engine finds first.</summary>
        public const string AutoPortOption = "(auto-detect)";

        public SpdViewModel()
        {
            Rows = new ObservableCollection<DiagnosticRow>();
            Ports = new ObservableCollection<SpdPort>();
            PortChoices = new ObservableCollection<string>();
            RebootTargets = ["system", "bootloader", "recovery", "edl"];

            _selectedRebootTarget = RebootTargets[0];
            _selectedPort = AutoPortOption;
            _fdl1Path = string.Empty;
            _fdl2Path = string.Empty;

            RefreshPorts();

            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SpdReadInfo));
            HandshakeCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SpdHandshake, BuildParameters()));
            UploadFdlCommand = new AsyncRelayCommand(_ => UploadFdlAsync());
            ResetFrpCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Erase the FRP partitions on this Unisoc handset?\n\n" +
                "'frp' and 'persistent' are erased through fastboot. Calibration partitions are never touched. " +
                "This cannot be undone.",
                () => RunOperationAsync(OperationTypes.SpdResetFrp, WithConfirmation(BuildParameters()), confirmDataLoss: true)));
            FactoryResetCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Format this Unisoc handset?\n\n" +
                "Only userdata and cache are erased; persist, prodnv, nvram, nvdata and modem are protected. " +
                "Every file on the phone is lost.",
                () => RunOperationAsync(OperationTypes.SpdFactoryReset, WithConfirmation(BuildParameters()), confirmDataLoss: true)));
            RebootCommand = new AsyncRelayCommand(_ =>
            {
                Dictionary<string, string> parameters = BuildParameters();
                parameters["target"] = SelectedRebootTarget;
                return RunOperationAsync(OperationTypes.SpdReboot, parameters);
            });
            BrowseFdl1Command = new AsyncRelayCommand(_ =>
            {
                Fdl1Path = Browse("Select the FDL1 loader") ?? Fdl1Path;
                return Task.CompletedTask;
            });
            BrowseFdl2Command = new AsyncRelayCommand(_ =>
            {
                Fdl2Path = Browse("Select the FDL2 loader (optional)") ?? Fdl2Path;
                return Task.CompletedTask;
            });
            RefreshPortsCommand = new AsyncRelayCommand(_ =>
            {
                StatusMessage = $"{RefreshPorts()} Unisoc interface(s).";
                return Task.CompletedTask;
            });
        }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        /// <summary>Live list of Unisoc USB/COM interfaces found on this computer.</summary>
        public ObservableCollection<SpdPort> Ports { get; }

        public ObservableCollection<string> PortChoices { get; }

        public IReadOnlyList<string> RebootTargets { get; }

        private string _fdl1Path;

        public string Fdl1Path
        {
            get => _fdl1Path;
            set => SetProperty(ref _fdl1Path, value);
        }

        private string _fdl2Path;

        public string Fdl2Path
        {
            get => _fdl2Path;
            set => SetProperty(ref _fdl2Path, value);
        }

        private string _selectedPort;

        public string SelectedPort
        {
            get => _selectedPort;
            set => SetProperty(ref _selectedPort, value);
        }

        private string _selectedRebootTarget;

        public string SelectedRebootTarget
        {
            get => _selectedRebootTarget;
            set => SetProperty(ref _selectedRebootTarget, value);
        }

        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand HandshakeCommand { get; }

        public AsyncRelayCommand UploadFdlCommand { get; }

        public AsyncRelayCommand ResetFrpCommand { get; }

        public AsyncRelayCommand FactoryResetCommand { get; }

        public AsyncRelayCommand RebootCommand { get; }

        public AsyncRelayCommand BrowseFdl1Command { get; }

        public AsyncRelayCommand BrowseFdl2Command { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            ApplyRows(Rows, result);
            RefreshPorts();
        }

        private int RefreshPorts()
        {
            // Collected first, then handed to the dispatcher in one pass: the bound list is never
            // cleared and refilled from a worker while the screen is rendering.
            List<SpdPort> ports = new();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    ports.AddRange(SpdPortLocator.FindPorts());
                }
                catch (Exception ex)
                {
                    Services.Log.Warn($"Unisoc port enumeration failed: {ex.Message}");
                }
            }

            OnUiThread(() =>
            {
                Ports.Clear();
                PortChoices.Clear();
                PortChoices.Add(AutoPortOption);

                foreach (SpdPort port in ports)
                {
                    Ports.Add(port);
                    if (!string.IsNullOrEmpty(port.PortName) &&
                        port.PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        PortChoices.Add(port.PortName);
                    }
                }

                if (!PortChoices.Contains(SelectedPort))
                {
                    SelectedPort = AutoPortOption;
                }
            });

            return Ports.Count;
        }

        private Dictionary<string, string> BuildParameters()
        {
            Dictionary<string, string> parameters = new();

            if (!string.IsNullOrWhiteSpace(SelectedPort) &&
                !string.Equals(SelectedPort, AutoPortOption, StringComparison.Ordinal))
            {
                parameters["port"] = SelectedPort;
            }

            if (!string.IsNullOrWhiteSpace(Fdl1Path))
            {
                parameters["fdl1Path"] = Fdl1Path.Trim();
            }

            if (!string.IsNullOrWhiteSpace(Fdl2Path))
            {
                parameters["fdl2Path"] = Fdl2Path.Trim();
            }

            return parameters;
        }

        private static Dictionary<string, string> WithConfirmation(Dictionary<string, string> parameters)
        {
            parameters["confirm"] = "yes";
            return parameters;
        }

        private Task UploadFdlAsync()
        {
            if (string.IsNullOrWhiteSpace(Fdl1Path))
            {
                ErrorMessage = "Choose the FDL1 loader image first.";
                return Task.CompletedTask;
            }

            return RunOperationAsync(OperationTypes.SpdFdl, BuildParameters());
        }

        private static string? Browse(string title)
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = title,
                Filter = "Loader images (*.bin;*.fdl)|*.bin;*.fdl|All files (*.*)|*.*",
                CheckFileExists = true
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
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
