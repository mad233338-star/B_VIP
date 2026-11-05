using B_VIP.Core.Operations;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Bridges the module layer's step reports to the live console: each step becomes a coloured
    /// line with the same severity the operation reported, so a technician can follow the modem
    /// handshake or the FRP sequence while it runs.
    /// </summary>
    public sealed class ConsoleOperationObserver : IOperationObserver
    {
        private readonly IConsoleLogger? _log;

        /// <summary>The console is optional: without a logger the steps are simply not displayed.</summary>
        public ConsoleOperationObserver(IConsoleLogger? log)
        {
            _log = log;
        }

        public void Step(OperationStep step)
        {
            if (_log is null)
            {
                return;
            }

            LogLevel level = step.Kind switch
            {
                OperationStepKind.Success => LogLevel.Success,
                OperationStepKind.Warning => LogLevel.Warning,
                OperationStepKind.Error => LogLevel.Error,
                OperationStepKind.Process => LogLevel.Process,
                _ => LogLevel.Info
            };

            _log.Write(level, step.Message);
        }
    }
}
