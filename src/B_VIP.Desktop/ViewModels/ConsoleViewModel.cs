using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// Backs <c>LiveConsoleView</c>. Lines arrive in batches on the UI thread, so a fast command can
    /// emit hundreds of rows without a layout pass per row.
    /// </summary>
    public sealed class ConsoleViewModel : ObservableObject, IDisposable
    {
        /// <summary>Rows kept on screen; older rows fall off the top so memory stays flat.</summary>
        public const int MaxLines = 2000;

        private readonly IConsoleLogger _logger;
        private readonly Dispatcher? _dispatcher;
        private long _lastSequence;
        private bool _autoScroll = true;
        private bool _disposed;

        public ConsoleViewModel(IConsoleLogger logger)
        {
            _logger = logger;
            _dispatcher = System.Windows.Application.Current?.Dispatcher;
            Entries = new ObservableCollection<ConsoleLogEntry>();

            foreach (ConsoleLogEntry entry in logger.Backlog)
            {
                Entries.Add(entry);
                _lastSequence = Math.Max(_lastSequence, entry.Sequence);
            }

            logger.EntriesFlushed += OnEntriesFlushed;
        }

        public ObservableCollection<ConsoleLogEntry> Entries { get; }

        /// <summary>When set, the view keeps the newest line in sight as output streams in.</summary>
        public bool AutoScroll
        {
            get => _autoScroll;
            set => SetProperty(ref _autoScroll, value);
        }

        public int LineCount => Entries.Count;

        public string Counter => LineCount == 1 ? "1 line" : $"{LineCount} lines";

        /// <summary>
        /// The collection belongs to the dispatcher: a logger built without one (a headless test, a
        /// module harness) raises this on its own thread, and adding rows to a bound collection from
        /// there throws the cross-thread exception the shell cannot survive.
        /// </summary>
        private void OnEntriesFlushed(object? sender, IReadOnlyList<ConsoleLogEntry> batch)
        {
            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(batch)));
                }
                catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
                {
                    // The window is closing.
                }

                return;
            }

            Apply(batch);
        }

        /// <summary>Appends one flushed batch, then trims the window in a single pass.</summary>
        private void Apply(IReadOnlyList<ConsoleLogEntry> batch)
        {
            if (_disposed)
            {
                return;
            }

            int added = 0;
            foreach (ConsoleLogEntry entry in batch)
            {
                // Lines written before this view existed are already in the seeded backlog; the
                // batch that was still in flight carries the same ids and must not be shown twice.
                if (entry.Sequence != 0 && entry.Sequence <= _lastSequence)
                {
                    continue;
                }

                Entries.Add(entry);
                _lastSequence = Math.Max(_lastSequence, entry.Sequence);
                added++;
            }

            if (added == 0)
            {
                return;
            }

            int overflow = Entries.Count - MaxLines;
            for (int i = 0; i < overflow; i++)
            {
                Entries.RemoveAt(0);
            }

            OnPropertyChanged(nameof(LineCount));
            OnPropertyChanged(nameof(Counter));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _logger.EntriesFlushed -= OnEntriesFlushed;
        }
    }
}
