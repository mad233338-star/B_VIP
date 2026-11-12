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

            // Every operation button is clickable with no handset attached: it listens for the 9008
            // port and runs the moment one answers. Only a running routine disables the panel.
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.QualcommReadInfo,
                prepare: EdlListener, caption: "Read info (EDL)"), _ => !IsBusy);
            SaharaCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.QualcommSahara, BuildParameters(),
                prepare: EdlListener, caption: "Sahara handshake"), _ => !IsBusy);
            GptCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.QualcommGpt, BuildParameters(),
                prepare: EdlListener, caption: "Read GPT"), _ => !IsBusy);

            FactoryResetCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Format the handset through EDL?\n\n" +
                "userdata and cache are erased; modemst1, modemst2, fsg and persist are never touched. " +
                "Every file on the phone is lost.",
                () => RunOperationAsync(OperationTypes.QualcommFactoryReset,
                    WithConfirmation(BuildParameters()), confirmDataLoss: true,
                    prepare: EdlListener, caption: "Factory format (EDL)")), _ => !IsBusy);
            RebootCommand = new AsyncRelayCommand(_ =>
            {
                Dictionary<string, string> parameters = BuildParameters();
                parameters["target"] = SelectedPowerTarget;
                return RunOperationAsync(OperationTypes.QualcommReboot, parameters,
                    prepare: EdlListener, caption: $"Power command ({SelectedPowerTarget})");
            }, _ => !IsBusy);

            // The single Reset FRP action: fh_loader starts the selected programmer on the 9008 port
            // and erases the partitions straight from EDL, which is the only way in on a handset with
            // a locked bootloader. Always clickable — it listens for the port when nothing is attached.
            ResetFrpCommand = new AsyncRelayCommand(_ => ResetFrpAsync(), _ => !IsBusy);
            BrowseFirehoseCommand = new AsyncRelayCommand(_ =>
            {
                BrowseFirehose();
                return Task.CompletedTask;
            });
            BrowseRawProgramCommand = new AsyncRelayCommand(_ =>
            {
                BrowseXml("rawprogram0.xml", value => RawProgramPath = value);
                return Task.CompletedTask;
            });
            BrowsePatchCommand = new AsyncRelayCommand(_ =>
            {
                BrowseXml("patch0.xml", value => PatchPath = value);
                return Task.CompletedTask;
            });
            BrowseImageCommand = new AsyncRelayCommand(_ =>
            {
                BrowseImage();
                return Task.CompletedTask;
            });
            FlashFirmwareCommand = new AsyncRelayCommand(_ => FlashFirmwareAsync(), _ => !IsBusy);
            ReadPartitionCommand = new AsyncRelayCommand(_ => ReadPartitionAsync(), _ => !IsBusy);
            WritePartitionCommand = new AsyncRelayCommand(_ => WritePartitionAsync(), _ => !IsBusy);

            GateOnBusy(ReadInfoCommand, SaharaCommand, GptCommand, ResetFrpCommand, FactoryResetCommand,
                RebootCommand, FlashFirmwareCommand, ReadPartitionCommand, WritePartitionCommand);
            RefreshPortsCommand = new AsyncRelayCommand(_ =>
            {
                StatusMessage = $"{RefreshPorts()} Qualcomm interface(s).";
                return Task.CompletedTask;
            });
        }

        /// <summary>The prepare delegate every EDL routine uses: press first, plug the cable in after.</summary>
        private Func<CancellationToken, Task<bool>> EdlListener => FrpListener(FrpWaitMode.QualcommEdl);

        public ObservableCollection<DiagnosticRow> Rows { get; }

        /// <summary>Download state of the Qualcomm loader bundle (Loaders\Qualcomm).</summary>
        public LoaderPanelViewModel Loaders { get; }

        /// <summary>Live list of Qualcomm USB/COM interfaces found on this computer.</summary>
        public ObservableCollection<QualcommPort> Ports { get; }

        /// <summary>Ports offered for the Firehose stage after the programmer has started.</summary>
        public ObservableCollection<string> PortChoices { get; }

        public IReadOnlyList<string> PowerTargets { get; }

        private string _firehosePath;

        /// <summary>
        /// Programmer binary (prog_firehose_*.elf / .mbn) for the connected SoC. The absolute path is
        /// what the engine receives; the screen shows only <see cref="FirehoseName"/>.
        /// </summary>
        public string FirehosePath
        {
            get => _firehosePath;
            set
            {
                if (SetProperty(ref _firehosePath, value))
                {
                    OnPropertyChanged(nameof(FirehoseName));
                    OnPropertyChanged(nameof(FirehoseTooltip));
                }
            }
        }

        /// <summary>The programmer's bare file name, so no workshop folder appears on screen.</summary>
        public string FirehoseName => string.IsNullOrEmpty(_firehosePath) ? string.Empty : Path.GetFileName(_firehosePath);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? FirehoseTooltip => string.IsNullOrWhiteSpace(_firehosePath) ? null : _firehosePath;

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

        /// <summary>FRP through fh_loader on the 9008 port, using the Firehose programmer.</summary>
        public AsyncRelayCommand ResetFrpCommand { get; }

        public AsyncRelayCommand FactoryResetCommand { get; }

        public AsyncRelayCommand RebootCommand { get; }

        public AsyncRelayCommand BrowseFirehoseCommand { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

        public AsyncRelayCommand BrowseRawProgramCommand { get; }

        public AsyncRelayCommand BrowsePatchCommand { get; }

        public AsyncRelayCommand BrowseImageCommand { get; }

        /// <summary>fh_loader writes the images rawprogram0.xml names, applies patch0, then resets.</summary>
        public AsyncRelayCommand FlashFirmwareCommand { get; }

        /// <summary>Dumps one partition over the open Firehose link into Backups\Qualcomm.</summary>
        public AsyncRelayCommand ReadPartitionCommand { get; }

        /// <summary>Writes an image back into its partition. Calibration partitions are refused.</summary>
        public AsyncRelayCommand WritePartitionCommand { get; }

        private string _rawProgramPath = string.Empty;

        /// <summary>The rawprogram0.xml that came with the package. Only the file name is shown.</summary>
        public string RawProgramPath
        {
            get => _rawProgramPath;
            set
            {
                if (SetProperty(ref _rawProgramPath, value))
                {
                    OnPropertyChanged(nameof(RawProgramName));
                    OnPropertyChanged(nameof(RawProgramTooltip));
                }
            }
        }

        public string RawProgramName => string.IsNullOrEmpty(_rawProgramPath) ? string.Empty : Path.GetFileName(_rawProgramPath);

        public string? RawProgramTooltip => string.IsNullOrWhiteSpace(_rawProgramPath) ? null : _rawProgramPath;

        private string _patchPath = string.Empty;

        /// <summary>The matching patch0.xml; optional, but most packages need it.</summary>
        public string PatchPath
        {
            get => _patchPath;
            set
            {
                if (SetProperty(ref _patchPath, value))
                {
                    OnPropertyChanged(nameof(PatchName));
                    OnPropertyChanged(nameof(PatchTooltip));
                }
            }
        }

        public string PatchName => string.IsNullOrEmpty(_patchPath) ? string.Empty : Path.GetFileName(_patchPath);

        public string? PatchTooltip => string.IsNullOrWhiteSpace(_patchPath) ? null : _patchPath;

        private string _partitionName = string.Empty;

        /// <summary>Partition the read/write actions target, e.g. 'boot' or 'modem'.</summary>
        public string PartitionName
        {
            get => _partitionName;
            set => SetProperty(ref _partitionName, value.Trim());
        }

        private string _imagePath = string.Empty;

        /// <summary>Image written by "Write partition". The screen shows only its bare name.</summary>
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (SetProperty(ref _imagePath, value))
                {
                    OnPropertyChanged(nameof(ImageName));
                    OnPropertyChanged(nameof(ImageTooltip));
                }
            }
        }

        public string ImageName => string.IsNullOrEmpty(_imagePath) ? string.Empty : Path.GetFileName(_imagePath);

        public string? ImageTooltip => string.IsNullOrWhiteSpace(_imagePath) ? null : _imagePath;

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

        /// <summary>
        /// The EDL routine needs the 9008 COM port and the programmer. The selected Firehose port wins;
        /// otherwise the first COM interface this screen found is used, so the button works without
        /// the operator selecting anything a second time.
        /// </summary>
        private Dictionary<string, string> BuildLoaderParameters()
        {
            Dictionary<string, string> parameters = BuildParameters();
            parameters["confirm"] = "yes";

            string? port = NormaliseSelectedPort(SelectedFirehosePort)
                ?? PortChoices.Select(NormaliseSelectedPort).FirstOrDefault(p => p is not null)
                ?? Ports.Select(p => p.PortName).FirstOrDefault(PortLikePort);

            if (port is not null)
            {
                parameters["port"] = port;
            }

            return parameters;
        }

        private static string? NormaliseSelectedPort(string? value)
            => PortLikePort(value) ? value!.Trim() : null;

        private static bool PortLikePort(string? value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Trim().StartsWith("COM", StringComparison.OrdinalIgnoreCase);

        private async Task ResetFrpAsync()
        {
            MessageBoxResult answer = MessageBox.Show(
                "Reset FRP through fh_loader in EDL?\n\n" +
                "fh_loader starts the selected Firehose programmer on the 9008 COM port and erases 'frp' and 'config', " +
                "then resets the handset. The button waits up to 45 seconds for the 9008 port when no handset is " +
                "attached. This cannot be undone.\n\nContinue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            // Refresh the interface list first, so a handset that was already connected is used
            // instead of the listener waiting on a stale snapshot.
            RefreshPorts();
            OnUiThread(() => Rows.Clear());

            await RunOperationAsync(OperationTypes.QualcommFrpLoader, BuildLoaderParameters(), confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.QualcommEdl),
                caption: "Reset FRP (EDL)");
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

        private void BrowseXml(string fileName, Action<string> assign)
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = $"Select {fileName} from the firmware package",
                Filter = "Firehose XML (*.xml)|*.xml|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                assign(dialog.FileName);
            }
        }

        private void BrowseImage()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select the partition image to write",
                Filter = "Partition images (*.bin;*.img)|*.bin;*.img|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                ImagePath = dialog.FileName;
            }
        }

        private async Task FlashFirmwareAsync()
        {
            if (string.IsNullOrWhiteSpace(RawProgramPath))
            {
                ErrorMessage = "Select the rawprogram0.xml that came with this firmware package.";
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "Flash the firmware through EDL?\n\n" +
                $"Package: {Path.GetFileName(RawProgramPath)}\n" +
                (string.IsNullOrWhiteSpace(PatchPath) ? "No patch0.xml selected.\n" : $"Patch: {Path.GetFileName(PatchPath)}\n") +
                "\nEvery partition the XML names is rewritten. Do not unplug the cable while fh_loader runs. Continue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Flash cancelled.";
                return;
            }

            RefreshPorts();
            OnUiThread(() => Rows.Clear());

            Dictionary<string, string> parameters = BuildLoaderParameters();
            parameters["rawProgram"] = RawProgramPath.Trim();
            if (!string.IsNullOrWhiteSpace(PatchPath))
            {
                parameters["patch"] = PatchPath.Trim();
            }

            await RunOperationAsync(OperationTypes.QualcommFlashFirmware, parameters, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.QualcommEdl), caption: "Flash firmware (EDL)");
        }

        private async Task ReadPartitionAsync()
        {
            if (string.IsNullOrWhiteSpace(PartitionName))
            {
                ErrorMessage = "Type the partition to dump (for example 'boot' or 'modem').";
                return;
            }

            RefreshPorts();
            OnUiThread(() => Rows.Clear());

            Dictionary<string, string> parameters = BuildParameters();
            parameters["partition"] = PartitionName;

            await RunOperationAsync(OperationTypes.QualcommReadPartition, parameters,
                prepare: FrpListener(FrpWaitMode.QualcommEdl), caption: $"Read {PartitionName}");
        }

        private async Task WritePartitionAsync()
        {
            if (string.IsNullOrWhiteSpace(PartitionName) || string.IsNullOrWhiteSpace(ImagePath))
            {
                ErrorMessage = "Name the target partition and choose the image to write.";
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                $"Write '{Path.GetFileName(ImagePath)}' into partition '{PartitionName}'?\n\n" +
                "The handset's own partition table is used to place the write, and the image must fit. " +
                "A wrong image can leave the phone unbootable. Continue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Write cancelled.";
                return;
            }

            RefreshPorts();
            OnUiThread(() => Rows.Clear());

            Dictionary<string, string> parameters = BuildParameters();
            parameters["confirm"] = "yes";
            parameters["partition"] = PartitionName;
            parameters["imagePath"] = ImagePath.Trim();

            await RunOperationAsync(OperationTypes.QualcommWritePartition, parameters, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.QualcommEdl), caption: $"Write {PartitionName}");
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
