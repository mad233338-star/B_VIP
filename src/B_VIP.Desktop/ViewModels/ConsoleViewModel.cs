using System.Collections.ObjectModel;
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
        private long _lastSequence;
        private bool _autoScroll = true;
        private bool _disposed;

        public ConsoleViewModel(IConsoleLogger logger)
        {
            _logger = logger;
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

        private void OnEntriesFlushed(object? sender, IReadOnlyList<ConsoleLogEntry> batch)
        {
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
            }

            while (Entries.Count > MaxLines)
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
