using System.IO;
using System.Net.Http;
using B_VIP.CloudClient;
using B_VIP.Contracts.Devices;
using B_VIP.Contracts.Operations;
using B_VIP.Contracts.Reports;
using B_VIP.Desktop.Mvvm;
using B_VIP.Hardware;
using B_VIP.Modules;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Runs device work on this machine and mirrors it to the backend: every execution is created
    /// as an operation, its real result is recorded and, when diagnostics are involved, a report is
    /// stored. When the server is unreachable the work still runs locally and the history says so.
    /// </summary>
    public sealed class DeviceWorkspace : ObservableObject
    {
        private readonly IDeviceDetector _detector;
        private readonly IDeviceDiagnostics _diagnostics;
        private readonly IToolLocator _tools;
        private readonly OperationExecutorRegistry _registry;
        private readonly BVipApiClient _api;
        private readonly SessionService _session;
        private readonly IConsoleLogger? _log;
        private readonly List<LocalOperation> _history = new();

        public DeviceWorkspace(IDeviceDetector detector, IDeviceDiagnostics diagnostics,
            IToolLocator tools, OperationExecutorRegistry registry, BVipApiClient api, SessionService session,
            IConsoleLogger? log = null)
        {
            _detector = detector;
            _diagnostics = diagnostics;
            _tools = tools;
            _registry = registry;
            _api = api;
            _session = session;
            _log = log;
        }

        public event Action? DevicesChanged;

        public IReadOnlyList<LocalOperation> History
        {
            get
            {
                lock (_history)
                {
                    return _history.ToList();
                }
            }
        }

        public async Task<DeviceListResponse> ScanAsync(CancellationToken ct = default)
        {
            DeviceListResponse response = await _detector.ScanAsync(ct);
            DevicesChanged?.Invoke();

            if (response.ScanError is not null)
            {
                _log?.Warn($"Device scan: {response.ScanError}");
            }
            else
            {
                _log?.Process($"Device scan: {response.Devices.Count} device(s) in {response.DurationMs} ms.");
            }

            return response;
        }

        public Task<IReadOnlyList<ToolStatusDto>> GetToolStatusAsync(CancellationToken ct = default)
            => _tools.GetAllStatusAsync(ct);

        public Task<DeviceDiagnosticsResponse> RunDiagnosticsAsync(string serial, CancellationToken ct = default)
            => _diagnostics.RunAsync(serial, ct);

        public Task<DeviceActionResult> ExecuteActionAsync(string serial, string action, string? argument, CancellationToken ct = default)
            => _diagnostics.ExecuteActionAsync(serial, action, argument, ct);

        /// <summary>Creates the operation on the server, runs it locally and records the real outcome.</summary>
        public async Task<OperationExecutionResult> ExecuteAsync(
            string operationType,
            string? serial,
            string? deviceId,
            string? deviceDescription,
            IReadOnlyDictionary<string, string>? parameters,
            bool confirmDataLoss,
            CancellationToken ct = default)
        {
            // The observer streams the module's own steps (COM port, AT command, adb call) into the
            // console while the operation is still running.
            OperationExecutionRequest request = new(operationType, serial,
                parameters ?? new Dictionary<string, string>(), Guid.NewGuid().ToString("N"),
                new ConsoleOperationObserver(_log));

            // Logged before the server round trip: the console must show the routine immediately,
            // not after a cold backend has answered the create-operation call.
            _log?.Process($"Executing {operationType} on {deviceDescription ?? serial ?? "the connected device"}...");

            Guid? operationId = null;
            try
            {
                OperationCreatedResponse created = await _api.CreateOperationAsync(new CreateOperationRequest(
                    operationType, deviceId, deviceDescription, parameters, confirmDataLoss), ct);
                operationId = created.Id;
            }
            catch (ApiException)
            {
                // Offline or unauthenticated: execute locally and mark the record accordingly.
            }
            catch (HttpRequestException)
            {
            }

            OperationExecutionResult result = await _registry.ExecuteAsync(request, ct);

            if (result.Succeeded)
            {
                _log?.Success($"{operationType} completed — {result.Summary}");
            }
            else
            {
                string detail = result.Errors.Count > 0 ? result.Errors[0] : result.Summary;
                _log?.Error($"{operationType} ended with {result.Outcome}: {detail}");
            }

            if (operationId.HasValue)
            {
                try
                {
                    await _api.RecordOperationResultAsync(operationId.Value, new RecordOperationResultRequest(
                        ExitCode: result.Succeeded ? 0 : 1,
                        TimedOut: result.Outcome == Core.OperationOutcome.TimedOut,
                        Cancelled: result.Outcome == Core.OperationOutcome.Cancelled,
                        Output: string.Join(Environment.NewLine, result.Log),
                        ErrorOutput: string.Join(Environment.NewLine, result.Errors),
                        Summary: result.Summary,
                        Verified: result.Succeeded), ct);
                }
                catch (ApiException)
                {
                }
                catch (HttpRequestException)
                {
                }
            }

            LocalOperation local = new(
                operationId,
                operationType,
                deviceDescription ?? serial ?? "—",
                result.Outcome.ToString(),
                result.Summary,
                DateTime.UtcNow,
                result.Errors.Count == 0 ? null : result.Errors[0]);

            lock (_history)
            {
                _history.Insert(0, local);
                if (_history.Count > 200)
                {
                    _history.RemoveRange(200, _history.Count - 200);
                }
            }

            return result;
        }

        /// <summary>Stores a diagnostic report on the server and returns its id when the call worked.</summary>
        public async Task<Guid?> SaveReportAsync(string title, string module, string? deviceId,
            string contentJson, bool containsSensitive, bool redact, CancellationToken ct = default)
        {
            try
            {
                DiagnosticReportDto report = await _api.CreateReportAsync(
                    new CreateReportRequest(title, module, deviceId, contentJson, containsSensitive, redact), ct);
                return report.Id;
            }
            catch (ApiException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }
    }

    public sealed record LocalOperation(
        Guid? ServerId,
        string OperationType,
        string Device,
        string Outcome,
        string Summary,
        DateTime OccurredUtc,
        string? Error);
}
