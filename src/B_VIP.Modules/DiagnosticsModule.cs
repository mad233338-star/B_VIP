using System.Text.Json;
using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>Runs the full Android diagnostic suite and converts it into an operation result.</summary>
    public sealed class AndroidDiagnosticExecutor : IOperationExecutor
    {
        private readonly IDeviceDiagnostics _diagnostics;

        public AndroidDiagnosticExecutor(IDeviceDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.AndroidDiagnosticRun];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (request.Serial is null)
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    "A connected device is required for diagnostics.", "No device serial was supplied.");
            }

            DeviceDiagnosticsResponse response = await _diagnostics.RunAsync(request.Serial, ct);

            List<string> log = [$"$ diagnostic suite for {response.DeviceDescription ?? request.Serial}"];
            foreach (DiagnosticCheckDto check in response.Checks)
            {
                log.Add($"[{check.Status.ToUpperInvariant()}] {check.Name}: {check.Detail}");
            }

            int failed = response.Checks.Count(c => c.Status.Equals("fail", StringComparison.OrdinalIgnoreCase));
            int warned = response.Checks.Count(c => c.Status.Equals("warn", StringComparison.OrdinalIgnoreCase));
            bool succeeded = response.Errors.Count == 0;

            string summary = succeeded
                ? $"{response.Checks.Count} check(s) executed: {failed} failed, {warned} warning(s)."
                : response.Errors[0];

            return new OperationExecutionResult(request.OperationType, succeeded,
                succeeded ? (warned > 0 ? OperationOutcome.RequiresAttention : OperationOutcome.Succeeded) : OperationOutcome.Failed,
                summary, response.Checks, log, response.Warnings, response.Errors);
        }
    }

    /// <summary>Reports the presence and version of the external tools the program depends on.</summary>
    public sealed class ToolProbeExecutor : IOperationExecutor
    {
        private readonly IToolLocator _tools;

        public ToolProbeExecutor(IToolLocator tools)
        {
            _tools = tools;
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.ToolProbe];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            IReadOnlyList<ToolStatusDto> tools = await _tools.GetAllStatusAsync(ct);
            List<DiagnosticCheckDto> checks = tools
                .Select(t => new DiagnosticCheckDto($"tool:{t.Tool}", t.Found ? "pass" : "fail",
                    t.Found ? $"{t.Path} ({t.Version ?? "version unknown"})" : t.Error, null))
                .ToList();

            List<string> log = checks.Select(c => $"{c.Name} -> {c.Detail}").ToList();
            bool allFound = tools.All(t => t.Found);

            return new OperationExecutionResult(request.OperationType, allFound,
                allFound ? OperationOutcome.Succeeded : OperationOutcome.RequiresAttention,
                allFound ? "All required external tools were found." : "One or more external tools are missing.",
                checks, log,
                allFound ? Array.Empty<string>() : new[] { "Install Android platform-tools and keep them on PATH." },
                Array.Empty<string>());
        }
    }
}
