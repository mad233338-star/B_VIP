using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;
using B_VIP.Desktop.Services.Samsung;
using B_VIP.Hardware;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>One row of the "connected devices" header: a COM port Windows reports right now.</summary>
    public sealed record OdinPortRow(string Summary, string Detail, string Badge, string Status);

    /// <summary>
    /// The Samsung Odin screen: four firmware pickers (BL / AP / CP / CSC), an optional PIT, the two
    /// Odin options and the bundled CLI flasher. The engine is the flasher the release ships under
    /// <c>Tools\Samsung</c>; its stdout/stderr streams into the screen's own console while it runs,
    /// and the cancel button terminates the process tree.
    /// </summary>
    public sealed class SamsungOdinViewModel : DeviceScreenViewModel
    {
        /// <summary>A full four-file flash can legitimately run for half an hour on a slow handset.</summary>
        private static readonly TimeSpan FlashTimeout = TimeSpan.FromMinutes(30);

        private const int MaxLogLines = 2000;

        private readonly List<OdinSlotViewModel> _slotList;

        private CancellationTokenSource? _cts;
        private OdinEngineInfo _engine;
        private bool _isFlashing;
        private bool _autoReboot = true;
        private bool _resetTime = true;
        private bool _usePit;
        private bool _hasPitFile;
        private bool _isSubscribed;
        private double _progress;
        private string _progressText = "Idle";
        private string _verdictText = string.Empty;
        private string _verdictStatus = string.Empty;
        private string _pitPath = string.Empty;
        private int _stageBase;

        public SamsungOdinViewModel()
        {
            _engine = OdinEngine.Resolve();

            _slotList =
            [
                new OdinSlotViewModel("BL", "Bootloader", "*.tar;*.md5;*.bin",
                    "Bootloader package. Usually mandatory: it carries the secondary bootloader the flash writes first.", OnSlotChanged),
                new OdinSlotViewModel("AP", "System", "*.tar;*.md5",
                    "AP package: system, boot, recovery and super. This is the firmware itself.", OnSlotChanged),
                new OdinSlotViewModel("CP", "Modem / Radio", "*.tar;*.md5",
                    "CP package: modem and baseband. Optional when only the system is being repaired.", OnSlotChanged),
                new OdinSlotViewModel("CSC", "Region / Data", "*.tar;*.md5",
                    "CSC package: regional configuration. The HOME_CSC variant keeps user data.", OnSlotChanged)
            ];

            Slots = new ReadOnlyCollection<OdinSlotViewModel>(_slotList);
            Ports = new ObservableCollection<OdinPortRow>();
            Log = new ObservableCollection<ConsoleLogEntryAdapter>();

            BrowsePitCommand = new AsyncRelayCommand(_ => BrowsePitAsync());
            ClearPitCommand = new RelayCommand(_ => ClearPit());
            StartFlashCommand = new AsyncRelayCommand(_ => StartFlashAsync(), _ => !IsFlashing);
            CancelCommand = new RelayCommand(_ => Cancel(), _ => IsFlashing);
            ResetCommand = new RelayCommand(_ => Reset());
            RefreshPortsCommand = new AsyncRelayCommand(_ => RefreshPortsAsync());
            OpenEngineFolderCommand = new RelayCommand(_ => OdinEngine.TryOpenFolder(EngineFolder));
        }

        /// <summary>The four firmware slots, in the order Odin presents them.</summary>
        public IReadOnlyList<OdinSlotViewModel> Slots { get; }

        /// <summary>Whatever Windows currently reports, refreshed on every plug/unplug.</summary>
        public ObservableCollection<OdinPortRow> Ports { get; }

        /// <summary>The flash console: the engine's own output, line by line.</summary>
        public ObservableCollection<ConsoleLogEntryAdapter> Log { get; }

        public AsyncRelayCommand BrowsePitCommand { get; }

        public RelayCommand ClearPitCommand { get; }

        public AsyncRelayCommand StartFlashCommand { get; }

        public RelayCommand CancelCommand { get; }

        public RelayCommand ResetCommand { get; }

        public AsyncRelayCommand RefreshPortsCommand { get; }

        public RelayCommand OpenEngineFolderCommand { get; }

        // ---- engine -------------------------------------------------------------------------

        public string EngineFolder => _engine.Folder;

        public bool IsEngineAvailable => _engine.Found;

        public string EngineText => _engine switch
        {
            { Found: false } => "Engine not installed",
            { Kind: OdinEngineKind.Odin4 } => "Engine ready · CLI (Silent) · odin4",
            { Kind: OdinEngineKind.Heimdall } => "Engine ready · CLI (Silent) · Heimdall",
            _ => $"Engine ready · CLI (Silent) · {_engine.Name}"
        };

        public string EngineStatus => _engine.Found ? "pass" : "fail";

        public string EngineDetail => _engine.Detail;

        // ---- device header ------------------------------------------------------------------

        public bool IsDownloadModeConnected => Ports.Any(p => p.Status == "pass");

        /// <summary>The header line: "[COM3] Added!!" once a handset is in Download mode.</summary>
        public string DownloadModeText
        {
            get
            {
                OdinPortRow? download = Ports.FirstOrDefault(p => p.Status == "pass");
                if (download is not null)
                {
                    return download.Summary;
                }

                return Ports.Count > 0
                    ? $"{Ports.Count} USB interface(s) present, none in Download mode"
                    : "Waiting for a handset in Download mode";
            }
        }

        public string DownloadModeBadge => IsDownloadModeConnected ? "Connected" : "Waiting";

        public string DownloadModeStatus => IsDownloadModeConnected ? "pass" : "missing";

        // ---- options ------------------------------------------------------------------------

        /// <summary>Reboot the handset when the flash finishes (Odin's default, and ours).</summary>
        public bool AutoReboot
        {
            get => _autoReboot;
            set => SetProperty(ref _autoReboot, value);
        }

        /// <summary>Odin's "F. Reset Time": rewrites the partition timestamps after the flash.</summary>
        public bool ResetTime
        {
            get => _resetTime;
            set => SetProperty(ref _resetTime, value);
        }

        /// <summary>True when a PIT has been chosen; ticking the box opens the picker instead.</summary>
        public bool UsePit
        {
            get => _usePit;
            set
            {
                if (!SetProperty(ref _usePit, value))
                {
                    return;
                }

                if (value && !_hasPitFile)
                {
                    _ = BrowsePitAsync();
                }
            }
        }

        public string PitFileName => string.IsNullOrWhiteSpace(_pitPath) ? "No PIT selected" : Path.GetFileName(_pitPath);

        public string PitTooltip => string.IsNullOrWhiteSpace(_pitPath)
            ? "Optional partition information table. Leave empty to let the engine read the PIT from the handset."
            : _pitPath;

        public bool HasPit => !string.IsNullOrWhiteSpace(_pitPath);

        // ---- progress / verdict -------------------------------------------------------------

        public bool IsFlashing
        {
            get => _isFlashing;
            private set
            {
                if (!SetProperty(ref _isFlashing, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanCancel));
                StartFlashCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }

        public bool CanCancel => IsFlashing;

        /// <summary>Flash percentage, 0-100, driving the blue progress bar.</summary>
        public double Progress
        {
            get => _progress;
            private set => SetProperty(ref _progress, Math.Clamp(value, 0, 100));
        }

        public string ProgressText
        {
            get => _progressText;
            private set => SetProperty(ref _progressText, value);
        }

        /// <summary>"PASS!" or "FAIL!" — empty until the flash finishes.</summary>
        public string VerdictText
        {
            get => _verdictText;
            private set
            {
                if (SetProperty(ref _verdictText, value))
                {
                    OnPropertyChanged(nameof(HasVerdict));
                }
            }
        }

        public bool HasVerdict => !string.IsNullOrEmpty(_verdictText);

        /// <summary>"pass" / "fail" / "warn", consumed by the status brush converter.</summary>
        public string VerdictStatus
        {
            get => _verdictStatus;
            private set => SetProperty(ref _verdictStatus, value);
        }

        public int SelectedFileCount => _slotList.Count(s => s.HasFile);

        public string SelectionSummary => SelectedFileCount switch
        {
            0 => "No firmware selected",
            1 => "1 package selected",
            _ => $"{SelectedFileCount} packages selected"
        };

        // ---- navigation ---------------------------------------------------------------------

        public override async Task OnNavigatedToAsync()
        {
            await base.OnNavigatedToAsync();
            RefreshEngine();
            Subscribe();
            await RefreshPortsAsync();
        }

        public override Task OnNavigatedFromAsync()
        {
            Unsubscribe();
            return Task.CompletedTask;
        }

        private void Subscribe()
        {
            if (_isSubscribed)
            {
                return;
            }

            Services.Ports.DeviceConnectionChanged += OnPortsChanged;
            _isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed)
            {
                return;
            }

            Services.Ports.DeviceConnectionChanged -= OnPortsChanged;
            _isSubscribed = false;
        }

        private void OnPortsChanged(object? sender, PortChangeEventArgs e) => ApplyPorts(e.Snapshot.Ports);

        private async Task RefreshPortsAsync()
        {
            try
            {
                // The watcher sees Download mode before adb or fastboot answer; a scan here also
                // picks up a cable that was plugged in while the screen was closed.
                await Services.Monitor.ScanInterfacesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }

            ApplyPorts(Services.Ports.Current.Ports);
        }

        private void ApplyPorts(IReadOnlyList<DetectedPort> ports)
        {
            List<OdinPortRow> rows = ports
                .Select(port =>
                {
                    bool download = port.Category == PortCategory.SamsungDownload;
                    string portName = port.PortName ?? "USB";
                    return new OdinPortRow(
                        download ? $"[{portName}] Added!!" : $"[{portName}] — {port.DisplayName}",
                        port.Summary,
                        download ? "Download mode" : port.CategoryLabel,
                        download ? "pass" : "missing");
                })
                .OrderByDescending(row => row.Status == "pass")
                .ToList();

            OnUiThread(() =>
            {
                Ports.Clear();
                foreach (OdinPortRow row in rows)
                {
                    Ports.Add(row);
                }

                OnPropertyChanged(nameof(IsDownloadModeConnected));
                OnPropertyChanged(nameof(DownloadModeText));
                OnPropertyChanged(nameof(DownloadModeBadge));
                OnPropertyChanged(nameof(DownloadModeStatus));
            });
        }

        private void RefreshEngine()
        {
            _engine = OdinEngine.Resolve();
            OnPropertyChanged(nameof(EngineFolder));
            OnPropertyChanged(nameof(IsEngineAvailable));
            OnPropertyChanged(nameof(EngineText));
            OnPropertyChanged(nameof(EngineStatus));
            OnPropertyChanged(nameof(EngineDetail));
            StartFlashCommand.RaiseCanExecuteChanged();

            if (!_engine.Found)
            {
                Services.Log.Warn(_engine.Detail);
            }
        }

        // ---- firmware slots -----------------------------------------------------------------

        private void OnSlotChanged()
        {
            OnPropertyChanged(nameof(SelectedFileCount));
            OnPropertyChanged(nameof(SelectionSummary));
        }

        private async Task BrowsePitAsync()
        {
            await Task.CompletedTask;

            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select the PIT (partition information table)",
                Filter = "PIT table (*.pit)|*.pit|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                // Cancelling must untick the box too, otherwise the screen claims a PIT it lacks.
                if (!HasPit && _usePit)
                {
                    _usePit = false;
                    OnPropertyChanged(nameof(UsePit));
                }

                return;
            }

            _pitPath = dialog.FileName;
            _hasPitFile = true;
            _usePit = true;
            OnPropertyChanged(nameof(PitFileName));
            OnPropertyChanged(nameof(PitTooltip));
            OnPropertyChanged(nameof(HasPit));
            OnPropertyChanged(nameof(UsePit));
            Services.Log.Info($"Odin PIT selected: {Path.GetFileName(_pitPath)}.");
        }

        private void ClearPit()
        {
            _pitPath = string.Empty;
            _hasPitFile = false;
            _usePit = false;
            OnPropertyChanged(nameof(PitFileName));
            OnPropertyChanged(nameof(PitTooltip));
            OnPropertyChanged(nameof(HasPit));
            OnPropertyChanged(nameof(UsePit));
        }

        // ---- flashing -----------------------------------------------------------------------

        private async Task StartFlashAsync()
        {
            if (IsFlashing)
            {
                return;
            }

            ErrorMessage = null;
            VerdictText = string.Empty;
            VerdictStatus = string.Empty;

            if (!LicenseGuard.CanRunOperations(Services.Session.License, out string licenseReason))
            {
                ErrorMessage = licenseReason;
                Services.Log.Warn(licenseReason);
                Services.Navigation.NavigateTo("license-activation");
                return;
            }

            if (!_engine.Found)
            {
                ErrorMessage = _engine.Detail;
                RefreshEngine();
                return;
            }

            List<OdinSlotFile> slots = _slotList
                .Where(s => s.HasFile)
                .Select(s => new OdinSlotFile(s.Slot, s.Path))
                .ToList();

            if (slots.Count == 0)
            {
                ErrorMessage = "Select at least one firmware package (BL, AP, CP or CSC) before flashing.";
                return;
            }

            string? missing = slots.FirstOrDefault(s => !File.Exists(s.FilePath))?.FilePath;
            if (missing is not null)
            {
                ErrorMessage = $"The package '{Path.GetFileName(missing)}' is no longer on disk. Select it again.";
                return;
            }

            if (UsePit && !HasPit)
            {
                ErrorMessage = "The PIT option is ticked but no PIT file is selected. Choose one or untick the option.";
                return;
            }

            // The screen only drives engines it can supervise: no vendor window is ever opened, so
            // progress and the verdict always come from the engine's own streamed output.
            if (!_engine.SupportsCli)
            {
                ErrorMessage = _engine.Detail;
                RefreshEngine();
                return;
            }

            // A flash always replaces what is on the handset, so it asks once, like Odin does.
            string slotList = string.Join(", ", slots.Select(s => s.Slot));
            MessageBoxResult answer = MessageBox.Show(
                $"Flash {slotList} on the handset in Download mode?\n\n" +
                $"Engine: {_engine.Name}\n" +
                (HasPit ? $"PIT: {Path.GetFileName(_pitPath)}\n" : string.Empty) +
                (AutoReboot ? "The handset reboots when the flash completes.\n" : "The handset stays in Download mode.\n") +
                "\nDo not unplug the cable until the screen shows PASS!. Continue?",
                "B_VIP Tool — Samsung Odin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Flash cancelled.";
                return;
            }

            CancellationTokenSource cts = new();
            List<string> runLines = new();
            _cts = cts;
            IsFlashing = true;
            Progress = 0;
            _stageBase = 0;
            ProgressText = "Starting…";
            OnUiThread(() => Log.Clear());

            string? workingFolder = null;

            try
            {
                // Heimdall writes partition images, not Odin packages, so the tar archives are
                // unpacked into %TEMP%\BVIP_ODIN and their images are handed over one by one.
                // odin4 takes the packages directly and skips this step entirely.
                IReadOnlyList<string> arguments;
                if (_engine.Kind == OdinEngineKind.Heimdall)
                {
                    ProgressText = "Reading firmware…";
                    AppendLine("Heimdall writes partitions, so the selected packages are unpacked first.", LogLevel.Info);

                    OdinExtractionResult extraction = await OdinFirmwareExtractor.ExtractAsync(slots, null, cts.Token);
                    workingFolder = extraction.WorkingFolder;

                    foreach (string line in extraction.Log)
                    {
                        AppendLine(line, LogLevel.Process);
                    }

                    foreach (string warning in extraction.Warnings)
                    {
                        AppendLine(warning, LogLevel.Warning);
                    }

                    if (!extraction.Succeeded)
                    {
                        VerdictText = "FAIL!";
                        VerdictStatus = "fail";
                        ProgressText = "Firmware could not be read";
                        ErrorMessage = extraction.Detail;
                        Services.Log.Error($"Odin flash FAIL!: {extraction.Detail}");
                        return;
                    }

                    arguments = OdinEngine.BuildHeimdallArguments(
                        extraction.Images, UsePit ? _pitPath : null, AutoReboot);

                    AppendLine($"{extraction.Images.Count} partition image(s) ready for Heimdall.", LogLevel.Success);
                }
                else
                {
                    arguments = OdinEngine.BuildArguments(
                        _engine.Kind, slots, UsePit ? _pitPath : null, AutoReboot, ResetTime);
                }

                Services.Log.Process($"Odin: {Path.GetFileName(_engine.Executable)} {string.Join(' ', arguments)}");

                if (!IsDownloadModeConnected)
                {
                    // Same behaviour as the FRP buttons: press first, plug the cable in afterwards.
                    if (!await WaitForFrpConnectionAsync(FrpWaitMode.SamsungDownload, cts.Token))
                    {
                        VerdictText = "FAIL!";
                        VerdictStatus = "fail";
                        ErrorMessage = "No handset entered Download mode within 45 seconds.";
                        ProgressText = "No device";
                        return;
                    }
                }
                else
                {
                    Services.Log.Success($"Download mode confirmed: {DownloadModeText}.");
                }

                ProgressText = "Flashing…";

                ProcessRequest request = new(
                    _engine.Executable,
                    arguments,
                    _engine.Folder,
                    (int)FlashTimeout.TotalSeconds,
                    null,
                    new OdinSink(this, runLines));

                ProcessResult result = await Services.ProcessRunner.RunAsync(request, cts.Token);

                if (result.Cancelled)
                {
                    VerdictText = "CANCELLED";
                    VerdictStatus = "warn";
                    ProgressText = "Cancelled";
                    StatusMessage = "Flash cancelled by the operator.";
                    Services.Log.Warn("Odin flash cancelled.");
                    return;
                }

                if (result.TimedOut)
                {
                    VerdictText = "FAIL!";
                    VerdictStatus = "fail";
                    ProgressText = "Timed out";
                    ErrorMessage = result.FailureReason
                        ?? $"The engine exceeded its {FlashTimeout.TotalMinutes:0}-minute limit.";
                    return;
                }

                bool engineReportedFailure;
                lock (runLines)
                {
                    engineReportedFailure = runLines.Any(OdinEngine.LooksFailed);
                }

                if (result.Succeeded && !engineReportedFailure)
                {
                    Progress = 100;
                    VerdictText = "PASS!";
                    VerdictStatus = "pass";
                    ProgressText = "Completed";
                    StatusMessage = $"Flash finished in {result.DurationMs / 1000.0:0.0}s.";
                    Services.Log.Success($"Odin flash PASS! ({result.DurationMs / 1000.0:0.0}s).");
                }
                else
                {
                    VerdictText = "FAIL!";
                    VerdictStatus = "fail";
                    ProgressText = "Failed";
                    string reason = !result.Started
                        ? result.FailureReason ?? "The engine could not be started."
                        : FirstMeaningfulLine(result.StandardError, result.StandardOutput)
                          ?? $"The engine exited with code {result.ExitCode}.";
                    ErrorMessage = reason;
                    StatusMessage = "Flash failed.";
                    Services.Log.Error($"Odin flash FAIL!: {reason}");
                }
            }
            catch (OperationCanceledException)
            {
                VerdictText = "CANCELLED";
                VerdictStatus = "warn";
                ProgressText = "Cancelled";
                StatusMessage = "Flash cancelled.";
            }
            catch (Exception ex)
            {
                VerdictText = "FAIL!";
                VerdictStatus = "fail";
                ErrorMessage = ex.Message;
                Services.Log.Error($"Odin flash failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                }

                cts.Dispose();
                IsFlashing = false;

                // The unpacked images are large (several GB for a full AP) and worthless once the
                // flash is over, so the working folder never outlives the run.
                OdinFirmwareExtractor.Cleanup(workingFolder);
            }
        }

        private void Cancel()
        {
            try
            {
                _cts?.Cancel();
                ProgressText = "Cancelling…";
                Services.Log.Warn("Cancelling the Odin flash…");
            }
            catch (ObjectDisposedException)
            {
                // The flash finished between the check and the cancel.
            }
        }

        private void Reset()
        {
            if (IsFlashing)
            {
                Cancel();
                return;
            }

            foreach (OdinSlotViewModel slot in _slotList)
            {
                slot.Clear();
            }

            ClearPit();
            AutoReboot = true;
            ResetTime = true;
            Progress = 0;
            ProgressText = "Idle";
            VerdictText = string.Empty;
            VerdictStatus = string.Empty;
            ErrorMessage = null;
            StatusMessage = null;
            OnUiThread(() => Log.Clear());
            OnSlotChanged();
        }

        /// <summary>Appends one engine line, keeping the console bounded.</summary>
        private void AppendLine(string text, LogLevel level)
        {
            ConsoleLogEntryAdapter entry = new(DateTime.Now, level, text);
            OnUiThread(() =>
            {
                Log.Add(entry);
                while (Log.Count > MaxLogLines)
                {
                    Log.RemoveAt(0);
                }
            });

            // The global console keeps the same record, so a flash is in the operation diary too.
            Services.Log.Write(level, text);
        }

        private static string? FirstMeaningfulLine(params string[] texts)
        {
            foreach (string text in texts)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                string? line = text
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .LastOrDefault(l => l.Length > 0);

                if (line is not null)
                {
                    return line.Length > 240 ? line[..240] : line;
                }
            }

            return null;
        }

        /// <summary>Receives the engine's streams and turns them into log lines and a percentage.</summary>
        private sealed class OdinSink : IProcessOutputSink
        {
            private readonly SamsungOdinViewModel _owner;
            private readonly List<string> _lines;

            public OdinSink(SamsungOdinViewModel owner, List<string> lines)
            {
                _owner = owner;
                _lines = lines;
            }

            public void Starting(string executable, IReadOnlyList<string> arguments)
                => _owner.AppendLine($"Starting {Path.GetFileName(executable)} {string.Join(' ', arguments)}", LogLevel.Process);

            public void Line(bool isError, string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                string line = text.TrimEnd();
                lock (_lines)
                {
                    _lines.Add(line);
                }

                LogLevel level = isError
                    ? LogLevel.Error
                    : OdinEngine.LooksSuccessful(line) ? LogLevel.Success
                    : OdinEngine.LooksFailed(line) ? LogLevel.Warning
                    : LogLevel.Process;

                _owner.AppendLine(line, level);

                if (OdinEngine.ParsePercent(line) is not { } value)
                {
                    return;
                }

                int stage = OdinEngine.StageIndex(line);
                if (stage >= 0)
                {
                    _owner._stageBase = stage;
                }

                // Four slots share the bar; a stage's own percentage fills its quarter.
                double overall = Math.Clamp((_owner._stageBase + (value / 100.0)) * 25.0, 0, 100);
                UiThread.Invoke(() =>
                {
                    // Never walk the bar backwards: engines report per-slot percentages.
                    if (overall > _owner.Progress)
                    {
                        _owner.Progress = overall;
                    }

                    _owner.ProgressText = $"Flashing… {Math.Round(Math.Max(overall, value)):0}%";
                });
            }

            public void Finished(int? exitCode, long durationMs)
                => _owner.AppendLine($"Engine exited with code {exitCode?.ToString() ?? "?"} after {durationMs} ms.", LogLevel.Info);
        }
    }

    /// <summary>One firmware picker: the slot name, the chosen file and its two actions.</summary>
    public sealed class OdinSlotViewModel : ObservableObject
    {
        private readonly Action _onChanged;
        private string _path = string.Empty;

        public OdinSlotViewModel(string slot, string title, string filter, string hint, Action onChanged)
        {
            Slot = slot;
            Title = title;
            Filter = filter;
            Hint = hint;
            _onChanged = onChanged;
            BrowseCommand = new AsyncRelayCommand(_ => BrowseAsync());
            ClearCommand = new RelayCommand(_ => Clear());
        }

        /// <summary>BL, AP, CP or CSC — the name the engine's command line uses.</summary>
        public string Slot { get; }

        public string Title { get; }

        /// <summary>The dialog filter, e.g. <c>*.tar;*.md5</c>.</summary>
        public string Filter { get; }

        public string Hint { get; }

        public string Path
        {
            get => _path;
            private set
            {
                if (SetProperty(ref _path, value))
                {
                    OnPropertyChanged(nameof(FileName));
                    OnPropertyChanged(nameof(Tooltip));
                    OnPropertyChanged(nameof(HasFile));
                    _onChanged();
                }
            }
        }

        /// <summary>
        /// Only the file name is ever shown: a technician should not have the workshop's folder
        /// structure on screen, and the full path stays in the tooltip and in the command line.
        /// </summary>
        public string FileName => HasFile ? System.IO.Path.GetFileName(_path) : "No file selected";

        public string Tooltip => HasFile ? _path : Hint;

        public bool HasFile => !string.IsNullOrWhiteSpace(_path);

        public AsyncRelayCommand BrowseCommand { get; }

        public RelayCommand ClearCommand { get; }

        public void Clear() => Path = string.Empty;

        private async Task BrowseAsync()
        {
            await Task.CompletedTask;

            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = $"Select the {Slot} package ({Title})",
                Filter = $"Odin packages ({Filter})|{Filter}|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FileName))
            {
                Path = dialog.FileName;
            }
        }
    }

    /// <summary>
    /// A console line in the Odin log. It mirrors <see cref="ConsoleLogEntry"/> but keeps the
    /// formatting members the view binds to (timestamp text and severity text).
    /// </summary>
    public sealed record ConsoleLogEntryAdapter(DateTime Timestamp, LogLevel Level, string Message)
    {
        public string TimestampText => Timestamp.ToString("HH:mm:ss");

        public string LevelText => Level switch
        {
            LogLevel.Success => "PASS",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Process => "CMD",
            _ => "INFO"
        };
    }
}
