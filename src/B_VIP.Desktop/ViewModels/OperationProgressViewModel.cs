using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// The shell-wide task dispatcher behind the status-bar progress bar: it reports determinate
    /// progress (0-100) or an indeterminate spinner, and exposes the cancel action for the running
    /// operation. Only one operation runs at a time, so a second start while busy is refused instead
    /// of silently replacing the first token.
    /// </summary>
    public sealed class OperationProgressViewModel : ObservableObject
    {
        private readonly IConsoleLogger? _log;
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private bool _isIndeterminate;
        private double _percent;
        private string _caption = string.Empty;

        public OperationProgressViewModel(IConsoleLogger? log = null)
        {
            _log = log;
            CancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(CanCancel));
                    CancelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>True while the work cannot report a percentage (device wait, port scan, ...).</summary>
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            private set => SetProperty(ref _isIndeterminate, value);
        }

        public double Percent
        {
            get => _percent;
            private set => SetProperty(ref _percent, value);
        }

        public string Caption
        {
            get => _caption;
            private set => SetProperty(ref _caption, value);
        }

        public bool CanCancel => IsRunning && _cts is { IsCancellationRequested: false };

        public RelayCommand CancelCommand { get; }

        public Task<bool> RunAsync(string caption, Func<CancellationToken, Task> work, bool indeterminate = false)
        {
            ArgumentNullException.ThrowIfNull(work);
            return RunAsync(caption, (_, ct) => work(ct), indeterminate);
        }

        /// <summary>
        /// Runs <paramref name="work"/> with progress and cancellation wired up. Returns false when the
        /// operation was cancelled; exceptions still propagate to the caller after the bar is reset.
        /// </summary>
        public async Task<bool> RunAsync(string caption, Func<IProgress<int>, CancellationToken, Task> work, bool indeterminate = false)
        {
            ArgumentNullException.ThrowIfNull(work);

            if (IsRunning)
            {
                return false;
            }

            CancellationTokenSource cts = new();
            _cts = cts;
            Caption = caption;
            IsIndeterminate = indeterminate;
            Percent = 0;
            IsRunning = true;

            // Created on the UI thread, so every Report() is marshalled back before the bar repaints.
            // A report posted just before completion is delivered afterwards: dropping it here keeps
            // the finished operation from leaving a stale percentage behind.
            IProgress<int> reporter = new Progress<int>(value =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    Percent = Math.Clamp(value, 0, 100);
                }
            });

            try
            {
                await work(reporter, cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                _log?.Warn($"{caption} cancelled by the operator.");
                return false;
            }
            finally
            {
                _cts = null;
                IsRunning = false;
                IsIndeterminate = false;
                Caption = string.Empty;
                Percent = 0;
                cts.Dispose();
            }
        }

        private void Cancel()
        {
            if (!CanCancel || _cts is null)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation finished between the check and the cancel.
            }

            _log?.Warn($"Cancellation requested for '{Caption}'.");
            OnPropertyChanged(nameof(CanCancel));
            CancelCommand.RaiseCanExecuteChanged();
        }
    }
}
