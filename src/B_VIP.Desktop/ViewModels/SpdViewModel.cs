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
    /// Unisoc screen: lists the SPRD boot-ROM / diag interfaces, takes the FDL loader images that
    /// match the SoC, and drives the routines the engine implements. Every routine runs through
    /// <see cref="DeviceScreenViewModel.RunOperationAsync"/>, so the steps stream into the live
    /// console and the shell's progress bar carries the cancel action.
    /// </summary>
    public sealed class SpdViewModel : DeviceScreenViewModel
    {
        /// <summary>Keeps the routine on whichever SPD port the engine finds first.</summary>
        public const string AutoPortOption = "(auto-detect)";

        private readonly SpreadtrumService _spreadtrum;

        public SpdViewModel()
        {
            // Two loader sources feed the boot-ROM upload: the release bundle in Loaders\SPD and a
            // .pac firmware whose own FDL1/FDL2 are extracted on demand.
            _spreadtrum = new SpreadtrumService(Services.Loaders);
            Loaders = new LoaderPanelViewModel(_spreadtrum.Loaders, "Spreadtrum loader package");

            Rows = new ObservableCollection<DiagnosticRow>();
            Ports = new ObservableCollection<SpdPort>();
            PortChoices = new ObservableCollection<string>();
            RebootTargets = ["system", "bootloader", "recovery", "edl"];

            _selectedRebootTarget = RebootTargets[0];
            _selectedPort = AutoPortOption;
            _fdl1Path = string.Empty;
            _fdl2Path = string.Empty;
            _pacPath = string.Empty;
            _pacSummary = "Select a .pac firmware to pull its FDL1/FDL2 out of the packet.";

            RefreshPorts();

            // Every operation button is clickable with no handset attached: it listens for the SPD
            // boot-ROM port and runs the moment one answers. Only a running routine disables the
            // panel.
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SpdReadInfo,
                prepare: SpdListener, caption: "Read info (Unisoc)"), _ => !IsBusy);
            HandshakeCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SpdHandshake, BuildParameters(),
                prepare: SpdListener, caption: "BROM handshake"), _ => !IsBusy);
            UploadFdlCommand = new AsyncRelayCommand(_ => UploadFdlAsync(), _ => !IsBusy);
            // The single Reset FRP action: FDL1 and FDL2 are uploaded to the boot ROM at their base
            // addresses, then the FRP partition is erased and the handset reset. This works on a
            // locked bootloader, and it is always clickable: the routine listens for the SPD port.
            ResetFrpCommand = new AsyncRelayCommand(_ => ResetFrpAsync(), _ => !IsBusy);
            FactoryResetCommand = new AsyncRelayCommand(_ => ConfirmedAsync(
                "Format this Unisoc handset?\n\n" +
                "Only userdata and cache are erased; persist, prodnv, nvram, nvdata and modem are protected. " +
                "Every file on the phone is lost.",
                () => RunOperationAsync(OperationTypes.SpdFactoryReset, WithConfirmation(BuildParameters()),
                    confirmDataLoss: true, prepare: SpdListener, caption: "Factory format (SPD)")), _ => !IsBusy);
            RebootCommand = new AsyncRelayCommand(_ =>
            {
                Dictionary<string, string> parameters = BuildParameters();
                parameters["target"] = SelectedRebootTarget;
                return RunOperationAsync(OperationTypes.SpdReboot, parameters,
                    prepare: SpdListener, caption: $"Reboot to {SelectedRebootTarget}");
            }, _ => !IsBusy);
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
            SelectPacCommand = new AsyncRelayCommand(_ =>
            {
                SelectPac();
                return Task.CompletedTask;
            });
            ExtractPacCommand = new AsyncRelayCommand(_ => ExtractPacAsync(), _ => !IsBusy);
            RefreshPortsCommand = new AsyncRelayCommand(_ =>
            {
                StatusMessage = $"{RefreshPorts()} Unisoc interface(s).";
                return Task.CompletedTask;
            });

            GateOnBusy(ReadInfoCommand, HandshakeCommand, UploadFdlCommand, ResetFrpCommand,
                FactoryResetCommand, RebootCommand, ExtractPacCommand);
        }

        /// <summary>The prepare delegate every boot-ROM routine uses: press first, plug the cable in after.</summary>
        private Func<CancellationToken, Task<bool>> SpdListener => FrpListener(FrpWaitMode.SpreadtrumBrom);

        public ObservableCollection<DiagnosticRow> Rows { get; }

        /// <summary>Download state of the Unisoc loader bundle (Loaders\SPD).</summary>
        public LoaderPanelViewModel Loaders { get; }

        /// <summary>Live list of Unisoc USB/COM interfaces found on this computer.</summary>
        public ObservableCollection<SpdPort> Ports { get; }

        public ObservableCollection<string> PortChoices { get; }

        public IReadOnlyList<string> RebootTargets { get; }

        private string _fdl1Path;

        /// <summary>
        /// FDL1 loader image. The engine is handed the absolute path; the screen only ever shows
        /// <see cref="Fdl1Name"/>.
        /// </summary>
        public string Fdl1Path
        {
            get => _fdl1Path;
            set
            {
                if (SetProperty(ref _fdl1Path, value))
                {
                    OnPropertyChanged(nameof(Fdl1Name));
                    OnPropertyChanged(nameof(Fdl1Tooltip));
                }
            }
        }

        /// <summary>The FDL1 image's bare file name.</summary>
        public string Fdl1Name => string.IsNullOrEmpty(_fdl1Path) ? string.Empty : Path.GetFileName(_fdl1Path);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? Fdl1Tooltip => string.IsNullOrWhiteSpace(_fdl1Path) ? null : _fdl1Path;

        private string _fdl2Path;

        /// <summary>Optional FDL2 loader image; same display rule as <see cref="Fdl1Path"/>.</summary>
        public string Fdl2Path
        {
            get => _fdl2Path;
            set
            {
                if (SetProperty(ref _fdl2Path, value))
                {
                    OnPropertyChanged(nameof(Fdl2Name));
                    OnPropertyChanged(nameof(Fdl2Tooltip));
                }
            }
        }

        /// <summary>The FDL2 image's bare file name.</summary>
        public string Fdl2Name => string.IsNullOrEmpty(_fdl2Path) ? string.Empty : Path.GetFileName(_fdl2Path);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? Fdl2Tooltip => string.IsNullOrWhiteSpace(_fdl2Path) ? null : _fdl2Path;

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

        private string _pacPath;

        /// <summary>
        /// Firmware packet the FDL stages are extracted from. Kept absolute for the extractor; the
        /// field shows only <see cref="PacName"/>.
        /// </summary>
        public string PacPath
        {
            get => _pacPath;
            set
            {
                if (SetProperty(ref _pacPath, value))
                {
                    OnPropertyChanged(nameof(PacName));
                    OnPropertyChanged(nameof(PacTooltip));
                    ExtractPacCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>The packet's bare file name, so no workshop folder appears on screen.</summary>
        public string PacName => string.IsNullOrEmpty(_pacPath) ? string.Empty : Path.GetFileName(_pacPath);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? PacTooltip => string.IsNullOrWhiteSpace(_pacPath) ? null : _pacPath;

        private string _pacSummary;

        /// <summary>What the selected packet contains, and where its FDL stages landed.</summary>
        public string PacSummary
        {
            get => _pacSummary;
            private set => SetProperty(ref _pacSummary, value);
        }

        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand HandshakeCommand { get; }

        public AsyncRelayCommand UploadFdlCommand { get; }

        /// <summary>FRP through spd_dump with the FDL stages, for a handset with a locked bootloader.</summary>
        public AsyncRelayCommand ResetFrpCommand { get; }

        public AsyncRelayCommand FactoryResetCommand { get; }

        public AsyncRelayCommand RebootCommand { get; }

        public AsyncRelayCommand BrowseFdl1Command { get; }

        public AsyncRelayCommand BrowseFdl2Command { get; }

        /// <summary>Chooses the .pac firmware whose FDL1/FDL2 are extracted.</summary>
        public AsyncRelayCommand SelectPacCommand { get; }

        /// <summary>Reads the PAC header table and unpacks FDL1/FDL2 into %TEMP%\BVIP_SPD.</summary>
        public AsyncRelayCommand ExtractPacCommand { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

        /// <summary>
        /// Opening the screen also makes sure the loader bundle is on disk; a copy that is already
        /// there is reused, so an offline workshop still works.
        /// </summary>
        public override async Task OnNavigatedToAsync()
        {
            await base.OnNavigatedToAsync();
            await Loaders.EnsureReadyAsync();

            // The release bundle usually carries the SC9863A FDL stages: offer them straight away.
            if (Loaders.IsReady && string.IsNullOrWhiteSpace(Fdl1Path))
            {
                PrefillFromBundle();
            }
        }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            ApplyRows(Rows, result);
            RefreshPorts();
        }

        /// <summary>Offers the FDL1/FDL2 images the release bundle ships, when it has any.</summary>
        private void PrefillFromBundle()
        {
            try
            {
                if (!System.IO.Directory.Exists(Loaders.Directory))
                {
                    return;
                }

                string? fdl1 = System.IO.Directory
                    .EnumerateFiles(Loaders.Directory, "*fdl1*", SearchOption.AllDirectories)
                    .FirstOrDefault();
                string? fdl2 = System.IO.Directory
                    .EnumerateFiles(Loaders.Directory, "*fdl2*", SearchOption.AllDirectories)
                    .FirstOrDefault();

                Fdl1Path = fdl1 ?? string.Empty;
                Fdl2Path = fdl2 ?? string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Services.Log.Warn($"The bundled FDL images could not be listed: {ex.Message}");
            }
        }

        private void SelectPac()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select a Spreadtrum .pac firmware",
                Filter = "PAC firmware (*.pac)|*.pac|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                PacPath = dialog.FileName;
                PacSummary = $"{Path.GetFileName(PacPath)} selected. Extract the loader stages to use it.";
            }
        }

        /// <summary>
        /// Parses the selected packet and hands its FDL1/FDL2 to the boot-ROM upload. Only those two
        /// payloads are written, into %TEMP%\BVIP_SPD; the firmware file itself is only read.
        /// </summary>
        private async Task ExtractPacAsync()
        {
            if (string.IsNullOrWhiteSpace(PacPath))
            {
                ErrorMessage = "Choose a .pac firmware first.";
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            ExtractPacCommand.RaiseCanExecuteChanged();

            try
            {
                PacSummary = "Reading the PAC header table…";

                PacExtractionResult result = await _spreadtrum.ExtractLoadersAsync(PacPath);

                if (!result.Success)
                {
                    ErrorMessage = result.Error ?? "The PAC packet could not be read.";
                    PacSummary = "Extraction failed.";
                    Services.Log.Error($"PAC extraction failed: {result.Error}");
                    return;
                }

                Fdl1Path = result.Fdl1Path ?? Fdl1Path;
                Fdl2Path = result.Fdl2Path ?? Fdl2Path;

                PacSummary = string.IsNullOrEmpty(result.Fdl2Path)
                    ? $"{result.Entries.Count} header(s); FDL1 extracted from the packet (no FDL2 entry)."
                    : $"{result.Entries.Count} header(s); FDL1 and FDL2 extracted from the packet.";

                StatusMessage = "FDL loader stages are ready.";
                Services.Log.Success(
                    $"PAC loader stages extracted: FDL1 {Path.GetFileName(result.Fdl1Path)}, FDL2 {Path.GetFileName(result.Fdl2Path ?? "(none)")}.");
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                PacSummary = "Extraction failed.";
            }
            finally
            {
                IsBusy = false;
                ExtractPacCommand.RaiseCanExecuteChanged();
            }
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

        private async Task ResetFrpAsync()
        {
            // A handset that is already plugged in must not make the operator wait: refresh the
            // interface list and offer the bundled FDL stages before the listener starts.
            RefreshPorts();

            if (string.IsNullOrWhiteSpace(Fdl1Path) || string.IsNullOrWhiteSpace(Fdl2Path))
            {
                PrefillFromBundle();
            }

            if (string.IsNullOrWhiteSpace(Fdl1Path))
            {
                ErrorMessage = "No FDL1 loader is available. Install the Unisoc bundle, extract a .pac firmware, or choose the FDL images.";
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "Reset FRP through spd_dump?\n\n" +
                "FDL1 is uploaded at 0x50000000 and FDL2 at 0x9efffe00, then the 'frp' partition is erased and the " +
                "handset reset. The button waits up to 45 seconds for the SPD boot-ROM port. " +
                "This works on a locked bootloader and cannot be undone.\n\nContinue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            Dictionary<string, string> parameters = WithConfirmation(BuildParameters());
            parameters["partition"] = "frp";

            OnUiThread(() => Rows.Clear());
            await RunOperationAsync(OperationTypes.SpdFrpDump, parameters, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.SpreadtrumBrom),
                caption: "Reset FRP (spd_dump)");
        }

        private Task UploadFdlAsync()
        {
            if (string.IsNullOrWhiteSpace(Fdl1Path))
            {
                ErrorMessage = "Choose the FDL1 loader image first.";
                return Task.CompletedTask;
            }

            return RunOperationAsync(OperationTypes.SpdFdl, BuildParameters(),
                prepare: SpdListener, caption: "Upload FDL1 / FDL2");
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
