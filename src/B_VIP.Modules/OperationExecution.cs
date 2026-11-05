using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;

namespace B_VIP.Modules
{
    public sealed record OperationExecutionRequest(
        string OperationType,
        string? Serial,
        IReadOnlyDictionary<string, string> Parameters,
        string CorrelationId,
        IOperationObserver? Observer = null)
    {
        public string? GetParameter(string name)
            => Parameters.TryGetValue(name, out string? value) ? value : null;
    }

    public sealed record OperationExecutionResult(
        string OperationType,
        bool Succeeded,
        OperationOutcome Outcome,
        string Summary,
        IReadOnlyList<DiagnosticCheckDto> Checks,
        IReadOnlyList<string> Log,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors)
    {
        public static OperationExecutionResult Failure(string operationType, string summary, params string[] errors)
            => new(operationType, false, OperationOutcome.Failed, summary,
                Array.Empty<DiagnosticCheckDto>(), Array.Empty<string>(), Array.Empty<string>(), errors);

        public static OperationExecutionResult Skipped(string operationType, string summary, string reason)
            => new(operationType, false, OperationOutcome.RequiresAttention, summary,
                Array.Empty<DiagnosticCheckDto>(), Array.Empty<string>(), new[] { reason }, Array.Empty<string>());
    }

    /// <summary>Reports a step through the request's observer, if the caller supplied one.</summary>
    public static class OperationExecutionRequestExtensions
    {
        public static void Process(this OperationExecutionRequest request, string message)
            => request.Observer?.Step(new OperationStep(OperationStepKind.Process, message));

        public static void Info(this OperationExecutionRequest request, string message)
            => request.Observer?.Step(new OperationStep(OperationStepKind.Info, message));

        public static void Success(this OperationExecutionRequest request, string message)
            => request.Observer?.Step(new OperationStep(OperationStepKind.Success, message));

        public static void Warning(this OperationExecutionRequest request, string message)
            => request.Observer?.Step(new OperationStep(OperationStepKind.Warning, message));

        public static void Error(this OperationExecutionRequest request, string message)
            => request.Observer?.Step(new OperationStep(OperationStepKind.Error, message));
    }

    /// <summary>Executes one operation type against real tools and devices.</summary>
    public interface IOperationExecutor
    {
        IReadOnlyList<string> SupportedOperations { get; }

        Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default);
    }

    /// <summary>Routes an operation request to the executor registered for its type.</summary>
    public sealed class OperationExecutorRegistry
    {
        private readonly Dictionary<string, IOperationExecutor> _executors = new(StringComparer.OrdinalIgnoreCase);

        public OperationExecutorRegistry(IEnumerable<IOperationExecutor> executors)
        {
            foreach (IOperationExecutor executor in executors)
            {
                foreach (string key in executor.SupportedOperations)
                {
                    _executors[key] = executor;
                }
            }
        }

        public IReadOnlyCollection<string> RegisteredOperationTypes => _executors.Keys;

        public bool CanExecute(string operationType) => _executors.ContainsKey(operationType);

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            if (!_executors.TryGetValue(request.OperationType, out IOperationExecutor? executor))
            {
                return OperationExecutionResult.Failure(request.OperationType,
                    $"No executor is registered for '{request.OperationType}'.",
                    $"No executor is registered for the operation type '{request.OperationType}' in this build.");
            }

            try
            {
                return await executor.ExecuteAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new OperationExecutionResult(request.OperationType, false, OperationOutcome.Cancelled,
                    "The operation was cancelled.", Array.Empty<DiagnosticCheckDto>(),
                    Array.Empty<string>(), Array.Empty<string>(), new[] { "Cancelled by the user." });
            }
            catch (Exception)
            {
                // Internal exception details are never surfaced to clients.
                return OperationExecutionResult.Failure(request.OperationType,
                    "The operation stopped because of an unexpected error.",
                    "The operation failed because of an unexpected error. See the server log for details.");
            }
        }
    }
}
