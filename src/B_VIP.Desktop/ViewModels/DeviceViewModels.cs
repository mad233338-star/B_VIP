using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using B_VIP.Application.Services;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;
using B_VIP.Hardware;
using B_VIP.Modules;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>Shared behaviour for screens that need a connected handset.</summary>
    public abstract class DeviceScreenViewModel : ViewModelBase
    {
        protected readonly AppServices Services;

        protected DeviceScreenViewModel()
        {
            Services = AppServices.Current;
            Devices = new ObservableCollection<DeviceInfoDto>();
            ToolStatuses = new ObservableCollection<ToolStatusDto>();
            ScanCommand = new AsyncRelayCommand(ScanAsync);
        }

        public ObservableCollection<DeviceInfoDto> Devices { get; }

        public ObservableCollection<ToolStatusDto> ToolStatuses { get; }

        /// <summary>
        /// Live USB/COM ports from the watcher. The collection updates itself on connect and
        /// disconnect, so the devices screen never needs a manual refresh.
        /// </summary>
        public DeviceMonitorService Monitor => Services.Monitor;

        public AsyncRelayCommand ScanCommand { get; }

        private DeviceInfoDto? _selectedDevice;

        public DeviceInfoDto? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    OnPropertyChanged(nameof(CanRunDeviceAction));
                    OnDeviceSelected(value);
                    RefreshDeviceGates();
                }
            }
        }

        public bool CanRunDeviceAction => _selectedDevice is not null;

        private readonly List<AsyncRelayCommand> _deviceGated = new();

        /// <summary>
        /// Registers the commands that need a selected handset. They are re-evaluated whenever the
        /// selection or the busy state changes, so a button never fires against "no device" and
        /// never starts a second routine while one is already running.
        /// </summary>
        protected void GateOnDevice(params AsyncRelayCommand[] commands)
        {
            _deviceGated.AddRange(commands);
            RefreshDeviceGates();
        }

        protected override void OnBusyChanged() => RefreshDeviceGates();

        private void RefreshDeviceGates()
        {
            foreach (AsyncRelayCommand command in _deviceGated)
            {
                command.RaiseCanExecuteChanged();
            }
        }

        public string this[string key] => Localizer.Instance[key];

        /// <summary>Runs <paramref name="action"/> on the UI thread, inline when already there.</summary>
        protected static void OnUiThread(Action action) => UiThread.Invoke(action);

        /// <summary>
        /// Turns an operation outcome into table rows. It only reads the result, so it can run on the
        /// worker that finished the operation; nothing it touches is bound to a control.
        /// </summary>
        protected static List<DiagnosticRow> BuildRows(OperationExecutionResult result)
        {
            List<DiagnosticRow> rows = new(result.Checks.Count + result.Log.Count + result.Warnings.Count);

            foreach (DiagnosticCheckDto check in result.Checks)
            {
                rows.Add(new DiagnosticRow
                {
                    Name = string.IsNullOrWhiteSpace(check.Name) ? "check" : check.Name,
                    Status = string.IsNullOrWhiteSpace(check.Status) ? "info" : check.Status,
                    Detail = check.Detail,
                    DurationMs = check.DurationMs
                });
            }

            foreach (string line in result.Log)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    rows.Add(new DiagnosticRow { Name = "log", Status = "info", Detail = line });
                }
            }

            foreach (string warning in result.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    rows.Add(new DiagnosticRow { Name = "notice", Status = "warn", Detail = warning });
                }
            }

            return rows;
        }

        /// <summary>
        /// Replaces a table's contents in one dispatcher pass. Mutating a bound collection from the
        /// thread that finished the operation, or clearing and re-adding it row by row while the grid
        /// is rendering, is what made WPF report an inconsistent items source.
        /// </summary>
        protected static void ApplyRows(ObservableCollection<DiagnosticRow> rows, OperationExecutionResult result)
            => ApplyRows(rows, BuildRows(result));

        protected static void ApplyRows(ObservableCollection<DiagnosticRow> rows, IReadOnlyList<DiagnosticRow> replacement)
        {
            OnUiThread(() =>
            {
                rows.Clear();
                foreach (DiagnosticRow row in replacement)
                {
                    rows.Add(row);
                }
            });
        }

        protected virtual void OnDeviceSelected(DeviceInfoDto? device)
        {
        }

        public override async Task OnNavigatedToAsync()
        {
            await ScanAsync();
            await LoadToolStatusAsync();
        }

        protected async Task ScanAsync(object? _ = null)
        {
            IsBusy = true;
            ErrorMessage = null;
            try
            {
                // A scan runs adb and fastboot in turn and can take seconds: drive it through the
                // shell dispatcher so the status bar shows it and the operator can cancel.
                bool entered = false;
                bool completed = await Services.Progress.RunAsync("Scanning for connected devices", async (_, ct) =>
                {
                    entered = true;
                    DeviceListResponse response = await Services.Devices.ScanAsync(ct);
                    string? previous = SelectedDevice?.Serial;

                    // The scan finishes wherever the detector's last await resumed: fill the bound
                    // collection on the dispatcher instead of assuming the UI thread.
                    OnUiThread(() =>
                    {
                        Devices.Clear();
                        foreach (DeviceInfoDto device in response.Devices)
                        {
                            Devices.Add(device);
                        }

                        SelectedDevice = previous is null
                            ? Devices.FirstOrDefault()
                            : Devices.FirstOrDefault(d => d.Serial == previous) ?? Devices.FirstOrDefault();

                        StatusMessage = Devices.Count == 0
                            ? Localizer.Instance["devices.none"]
                            : $"{Devices.Count} device(s) detected.";
                    });

                    ErrorMessage = response.ScanError;
                }, indeterminate: true);

                if (!completed && entered)
                {
                    StatusMessage = "Scan cancelled.";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadToolStatusAsync()
        {
            try
            {
                IReadOnlyList<ToolStatusDto> tools = await Services.Devices.GetToolStatusAsync();
                OnUiThread(() =>
                {
                    ToolStatuses.Clear();
                    foreach (ToolStatusDto tool in tools)
                    {
                        ToolStatuses.Add(tool);
                    }
                });
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        /// <summary>
        /// Runs one operation type through the shell dispatcher: the status bar shows the task, the
        /// cancel button kills the running tool, and the verdict is written to the live console next
        /// to the streamed command output.
        /// </summary>
        protected async Task RunOperationAsync(string operationType, IReadOnlyDictionary<string, string>? parameters = null, bool confirmDataLoss = false)
        {
            // A lapsed trial or subscription locks the hardware screens and sends the technician to
            // the renewal view instead of letting a command fail on the server.
            if (!LicenseGuard.CanRunOperations(Services.Session.License, out string licenseReason))
            {
                ErrorMessage = licenseReason;
                StatusMessage = "Opening License & Activation…";
                Services.Log.Warn(licenseReason);
                Services.Navigation.NavigateTo("license-activation");
                return;
            }

            IsBusy = true;
            ErrorMessage = null;

            string label = OperationTypes.Find(operationType)?.DisplayName ?? operationType;
            try
            {
                bool entered = false;
                bool completed = await Services.Progress.RunAsync(label, async (_, ct) =>
                {
                    entered = true;
                    OperationExecutionResult result = await Services.Devices.ExecuteAsync(
                        operationType,
                        SelectedDevice?.Serial,
                        SelectedDevice?.DeviceId,
                        Describe(SelectedDevice),
                        parameters,
                        confirmDataLoss,
                        ct);

                    StatusMessage = result.Summary;
                    if (result.Errors.Count > 0)
                    {
                        ErrorMessage = string.Join(Environment.NewLine, result.Errors);
                    }

                    // The screen is told about the outcome on the UI thread, whichever thread the
                    // module finished on, so it can touch its bound tables safely.
                    OnUiThread(() => OnOperationCompleted(result));
                    // DeviceWorkspace already writes the verdict to the console; logging it here too
                    // would double every line of the operation diary.
                }, indeterminate: true);

                if (!completed && entered)
                {
                    StatusMessage = "Operation cancelled.";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Services.Log.Error($"{label} failed to start: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        protected virtual void OnOperationCompleted(OperationExecutionResult result)
        {
        }

        protected static string? Describe(DeviceInfoDto? device)
            => device is null ? null : string.Join(" ", new[] { device.Manufacturer, device.Model, device.Serial }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public sealed class DeviceManagerViewModel : DeviceScreenViewModel
    {
        public DeviceManagerViewModel()
        {
            RebootCommand = new AsyncRelayCommand(_ => RunDeviceActionAsync("reboot"));
            RebootBootloaderCommand = new AsyncRelayCommand(_ => RunDeviceActionAsync("reboot-bootloader"));
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RunDeviceActionAsync("reboot-recovery"));
        }

        public AsyncRelayCommand RebootCommand { get; }

        public AsyncRelayCommand RebootBootloaderCommand { get; }

        public AsyncRelayCommand RebootRecoveryCommand { get; }

        private async Task RunDeviceActionAsync(string action)
        {
            if (SelectedDevice is not { Serial: { Length: > 0 } serial })
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return;
            }

            IsBusy = true;
            try
            {
                DeviceActionResult result = await Services.Devices.ExecuteActionAsync(serial, action, null);
                StatusMessage = result.Detail;
                if (!result.Succeeded)
                {
                    ErrorMessage = result.Detail;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    public sealed class DiagnosticRow
    {
        public string Name { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string? Detail { get; init; }

        public long? DurationMs { get; init; }
    }

    public sealed class DiagnosticsViewModel : DeviceScreenViewModel
    {
        private Guid? _lastReportId;

        public DiagnosticsViewModel()
        {
            RunCommand = new AsyncRelayCommand(_ => RunAsync());
            ExportCommand = new AsyncRelayCommand(_ => ExportAsync());
            Rows = new ObservableCollection<DiagnosticRow>();
        }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        public AsyncRelayCommand RunCommand { get; }

        public AsyncRelayCommand ExportCommand { get; }

        public Guid? LastReportId
        {
            get => _lastReportId;
            private set => SetProperty(ref _lastReportId, value, () => OnPropertyChanged(nameof(HasReport)));
        }

        public bool HasReport => _lastReportId.HasValue;

        private async Task RunAsync()
        {
            if (SelectedDevice is not { Serial: { Length: > 0 } serial })
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            Rows.Clear();
            try
            {
                DeviceDiagnosticsResponse response = await Services.Devices.RunDiagnosticsAsync(serial);
                ApplyRows(Rows, BuildDiagnosticRows(response));

                string json = ReportBuilder.BuildJson(new ReportInput(
                    Module: "android-diagnostics",
                    Title: $"Diagnostics — {Describe(SelectedDevice)}",
                    DeviceId: SelectedDevice.DeviceId,
                    DeviceDescription: Describe(SelectedDevice),
                    AppVersion: typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    OsVersion: Environment.OSVersion.ToString(),
                    OperationId: null,
                    CorrelationId: Guid.NewGuid().ToString("N"),
                    Checks: response.Checks.Select(c => new ReportCheckResult(c.Name, c.Status, c.Detail, c.DurationMs)).ToList(),
                    Warnings: response.Warnings,
                    Errors: response.Errors,
                    Extra: null), DateTime.UtcNow);

                LastReportId = await Services.Devices.SaveReportAsync(
                    $"Diagnostics — {Describe(SelectedDevice)}", "android-diagnostics",
                    SelectedDevice.DeviceId, json, containsSensitive: true, redact: false);

                StatusMessage = Rows.Count == 0 ? Localizer.Instance["common.empty"] : $"{Rows.Count} result(s).";
                ErrorMessage = response.Errors.Count > 0 ? string.Join(Environment.NewLine, response.Errors) : null;
                LastJson = json;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static List<DiagnosticRow> BuildDiagnosticRows(DeviceDiagnosticsResponse response)
        {
            List<DiagnosticRow> rows = new(response.Checks.Count + response.Warnings.Count);
            foreach (DiagnosticCheckDto check in response.Checks)
            {
                rows.Add(new DiagnosticRow
                {
                    Name = string.IsNullOrWhiteSpace(check.Name) ? "check" : check.Name,
                    Status = string.IsNullOrWhiteSpace(check.Status) ? "info" : check.Status,
                    Detail = check.Detail,
                    DurationMs = check.DurationMs
                });
            }

            foreach (string warning in response.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    rows.Add(new DiagnosticRow { Name = "warning", Status = "warn", Detail = warning });
                }
            }

            return rows;
        }

        public string LastJson { get; private set; } = "{}";

        private Task ExportAsync()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "B_VIP");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(path, LastJson);
                StatusMessage = path;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }

            return Task.CompletedTask;
        }
    }

    public enum VendorKind
    {
        Samsung,
        Xiaomi,
        Infinix,
        Tecno,
        MediaTek,
        Qualcomm,
        Spreadtrum
    }

    /// <summary>
    /// One action offered by a brand tab: the operation it starts, whether it erases data and the
    /// parameters that operation needs (a reboot target, a partition list).
    /// </summary>
    public sealed record VendorAction(
        string Title,
        string OperationType,
        bool ErasesData,
        IReadOnlyDictionary<string, string>? Parameters = null);

    /// <summary>
    /// A brand tab: reads the handset through its own module and offers the maintenance routines
    /// that module really implements. Destructive routines ask for confirmation first.
    /// </summary>
    public sealed class VendorViewModel : DeviceScreenViewModel
    {
        public VendorViewModel(VendorKind kind)
        {
            Kind = kind;
            Rows = new ObservableCollection<DiagnosticRow>();

            (string title, IReadOnlyList<VendorAction> actions) = kind switch
            {
                VendorKind.Samsung =>
                    ("Samsung", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.SamsungDiagnostic, false),
                        new VendorAction("Reboot to Download", OperationTypes.SamsungRebootDownload, false),
                        new VendorAction("Reboot to System", OperationTypes.VendorReboot, false, Target("system")),
                        new VendorAction("Reboot to Recovery", OperationTypes.VendorReboot, false, Target("recovery"))
                    }),
                VendorKind.Xiaomi =>
                    ("Xiaomi", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.XiaomiReadInfo, false),
                        new VendorAction("Read fastboot variables", OperationTypes.FastbootReadInfo, false),
                        new VendorAction("Reset FRP (frp + config)", OperationTypes.XiaomiResetFrp, true),
                        new VendorAction("Bypass Mi Account (ADB)", OperationTypes.XiaomiBypassMiAccount, false),
                        new VendorAction("Factory format (userdata + cache)", OperationTypes.FastbootEraseBatch, true,
                            Partitions("userdata", "cache")),
                        new VendorAction("Reboot to EDL", OperationTypes.VendorReboot, false, Target("edl")),
                        new VendorAction("Reboot to System", OperationTypes.VendorReboot, false, Target("system")),
                        new VendorAction("Reboot to Recovery", OperationTypes.VendorReboot, false, Target("recovery"))
                    }),
                VendorKind.Infinix =>
                    ("Infinix", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.InfinixReadInfo, false),
                        new VendorAction("Read fastboot variables", OperationTypes.FastbootReadInfo, false),
                        new VendorAction("Reset FRP (frp + persistent)", OperationTypes.FastbootEraseBatch, true,
                            Partitions("frp", "persistent")),
                        new VendorAction("Factory format (fastboot -w)", OperationTypes.InfinixFactoryFormat, true),
                        new VendorAction("Reboot to System", OperationTypes.VendorReboot, false, Target("system")),
                        new VendorAction("Reboot to Fastboot", OperationTypes.VendorReboot, false, Target("bootloader"))
                    }),
                VendorKind.Tecno =>
                    ("Tecno", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.TecnoReadInfo, false),
                        new VendorAction("Read fastboot variables", OperationTypes.FastbootReadInfo, false),
                        new VendorAction("Reset FRP (frp + persistent)", OperationTypes.FastbootEraseBatch, true,
                            Partitions("frp", "persistent")),
                        new VendorAction("Wipe userdata & cache", OperationTypes.TecnoWipe, true),
                        new VendorAction("Reboot to System", OperationTypes.VendorReboot, false, Target("system")),
                        new VendorAction("Reboot to Fastboot", OperationTypes.VendorReboot, false, Target("bootloader"))
                    }),
                VendorKind.MediaTek =>
                    ("MediaTek", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.MediaTekDiagnostic, false),
                        new VendorAction("Erase FRP (fastboot)", OperationTypes.FastbootEraseBatch, true, Partitions("frp")),
                        new VendorAction("Read fastboot variables", OperationTypes.FastbootReadInfo, false),
                        new VendorAction("Reboot to Bootloader", OperationTypes.VendorReboot, false, Target("bootloader"))
                    }),
                VendorKind.Qualcomm =>
                    ("Qualcomm", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.QualcommDiagnostic, false),
                        new VendorAction("Reboot to EDL", OperationTypes.VendorReboot, false, Target("edl")),
                        new VendorAction("Erase config + frp (fastboot)", OperationTypes.FastbootEraseBatch, true,
                            Partitions("config", "frp")),
                        new VendorAction("Read fastboot variables", OperationTypes.FastbootReadInfo, false)
                    }),
                _ =>
                    ("SPREADTRUM (Unisoc)", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.SpreadtrumReadInfo, false),
                        new VendorAction("Reset FRP (frp + persist)", OperationTypes.FastbootEraseBatch, true,
                            Partitions("frp", "persist")),
                        new VendorAction("Factory format (userdata + cache)", OperationTypes.FastbootEraseBatch, true,
                            Partitions("userdata", "cache")),
                        new VendorAction("Reboot to System", OperationTypes.VendorReboot, false, Target("system"))
                    })
            };

            Title = title;
            Actions = actions;
            RunCommand = new AsyncRelayCommand(
                _ => RunAsync(actions[0].OperationType, actions[0].ErasesData, actions[0].Parameters),
                _ => !IsBusy && CanRunDeviceAction);
            RunActionCommand = new AsyncRelayCommand(RunActionAsync, _ => !IsBusy && CanRunDeviceAction);

            // Both commands need a handset: the buttons stay disabled until one is selected.
            GateOnDevice(RunCommand, RunActionCommand);
        }

        private static Dictionary<string, string> Target(string target) => new() { ["target"] = target };

        private static Dictionary<string, string> Partitions(params string[] partitions)
            => new() { ["partitions"] = string.Join(',', partitions) };

        public VendorKind Kind { get; }

        public string Title { get; }

        public IReadOnlyList<VendorAction> Actions { get; }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        public AsyncRelayCommand RunCommand { get; }

        public AsyncRelayCommand RunActionCommand { get; }

        protected override void OnOperationCompleted(OperationExecutionResult result) => Fill(result);

        private async Task RunActionAsync(object? parameter)
        {
            if (parameter is not VendorAction action)
            {
                return;
            }

            if (action.ErasesData)
            {
                MessageBoxResult answer = MessageBox.Show(
                    $"{action.Title} erases protected partitions on the connected handset.\n\n" +
                    "This cannot be undone. Continue?",
                    "B_VIP Tool",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                {
                    StatusMessage = "Routine cancelled.";
                    return;
                }
            }

            Dictionary<string, string>? parameters = action.Parameters is null
                ? null
                : new Dictionary<string, string>(action.Parameters);

            // Destructive routines carry an explicit confirmation parameter so the module refuses
            // to erase anything when it is invoked without the operator's consent.
            if (action.ErasesData)
            {
                parameters ??= new Dictionary<string, string>();
                parameters["confirm"] = "yes";
            }

            await RunAsync(action.OperationType, action.ErasesData, parameters);
        }

        /// <summary>
        /// Runs the routine through the shared dispatcher so the status bar, the cancel button and
        /// the live console all see it: DeviceWorkspace logs the start line and the verdict, and the
        /// module streams its own steps while the operation is still running.
        /// </summary>
        private async Task RunAsync(string operationType, bool confirmDataLoss,
            IReadOnlyDictionary<string, string>? parameters)
        {
            if (SelectedDevice is null)
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return;
            }

            // The previous routine's rows must not stay on screen while this one runs.
            OnUiThread(() => Rows.Clear());
            await RunOperationAsync(operationType, parameters, confirmDataLoss);
        }

        private void Fill(OperationExecutionResult result) => ApplyRows(Rows, BuildVendorRows(result));

        /// <summary>
        /// The labels the technician expects instead of the raw getprop keys the module reports.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> RowLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["adb-status"] = "ADB Status",
                ["brand-identity"] = "Brand identity",
                ["board-platform"] = "Board platform",
                ["chipset-family"] = "Chipset family",
                ["ro.product.model"] = "Model",
                ["ro.product.brand"] = "Brand",
                ["ro.product.device"] = "Device",
                ["ro.build.display.id"] = "Build",
                ["ro.build.version.release"] = "Android version",
                ["ro.build.version.sdk"] = "API level",
                ["ro.build.version.security_patch"] = "Security patch",
                ["ro.build.fingerprint"] = "Fingerprint",
                ["ro.product.cpu.abi"] = "CPU ABI",
                ["ro.boot.hardware"] = "Boot hardware",
                ["ro.bootloader"] = "Bootloader",
                ["ro.boot.hardware.sku"] = "Hardware SKU",
                ["ro.miui.ui.version.name"] = "MIUI version",
                ["ro.mi.os.version.name"] = "HyperOS version",
                ["ro.miui.region"] = "Region",
                ["ro.serialno"] = "Serial",
                ["frp-state"] = "FRP state",
                ["oem-unlock"] = "OEM unlock",
                ["sprd-com-ports"] = "COM ports",
                ["sprd-diag-interface"] = "SPRD diag interface",
                ["fastboot-product"] = "Fastboot product",
                ["log"] = "Log",
                ["notice"] = "Notice",
                ["warning"] = "Warning",
                ["error"] = "Error"
            };

        /// <summary>
        /// Builds the table for a brand routine from the module's own outcome. It only reads the
        /// result, so it can run on the worker that finished the operation; <c>ApplyRows</c> then
        /// swaps the whole table in one dispatcher pass.
        /// </summary>
        private static List<DiagnosticRow> BuildVendorRows(OperationExecutionResult result)
        {
            List<DiagnosticRow> rows = BuildRows(result);

            foreach (string error in result.Errors)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    rows.Add(new DiagnosticRow { Name = "error", Status = "fail", Detail = error });
                }
            }

            // A read that produced nothing must still say so: an empty grid looks like a broken
            // screen, which is exactly how the Xiaomi tab behaved on an unauthorised handset.
            if (rows.Count == 0)
            {
                rows.Add(new DiagnosticRow
                {
                    Name = "Result",
                    Status = result.Succeeded ? "pass" : "fail",
                    Detail = result.Summary
                });
            }

            return rows.Select(Friendly).ToList();
        }

        /// <summary>Renames a raw check to its display label and drops the now duplicated prefix.</summary>
        private static DiagnosticRow Friendly(DiagnosticRow row)
        {
            string name = RowLabels.TryGetValue(row.Name, out string? label) ? label : row.Name;
            string? detail = row.Detail;

            if (!string.IsNullOrEmpty(detail) &&
                detail.StartsWith(name + ": ", StringComparison.OrdinalIgnoreCase))
            {
                detail = detail[(name.Length + 2)..];
            }

            return new DiagnosticRow { Name = name, Status = row.Status, Detail = detail, DurationMs = row.DurationMs };
        }
    }
}
