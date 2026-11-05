namespace B_VIP.Core.Operations
{
    public enum OperationStepKind
    {
        Process,
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>One line of progress reported from inside an operation while it is still running.</summary>
    public sealed record OperationStep(OperationStepKind Kind, string Message);

    /// <summary>
    /// Sink for step-by-step operation progress. The module layer reports what it is doing — which
    /// COM port it opened, which AT command it sent, which adb command failed — so the shell can
    /// mirror it into the live console without the modules knowing about WPF.
    /// </summary>
    public interface IOperationObserver
    {
        void Step(OperationStep step);
    }

    public static class OperationObserverExtensions
    {
        public static void Process(this IOperationObserver? observer, string message)
            => observer?.Step(new OperationStep(OperationStepKind.Process, message));

        public static void Info(this IOperationObserver? observer, string message)
            => observer?.Step(new OperationStep(OperationStepKind.Info, message));

        public static void Success(this IOperationObserver? observer, string message)
            => observer?.Step(new OperationStep(OperationStepKind.Success, message));

        public static void Warning(this IOperationObserver? observer, string message)
            => observer?.Step(new OperationStep(OperationStepKind.Warning, message));

        public static void Error(this IOperationObserver? observer, string message)
            => observer?.Step(new OperationStep(OperationStepKind.Error, message));
    }
}
