using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using System.Windows.Threading;

namespace B_VIP.Desktop.Services
{
    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error,
        Process
    }

    /// <summary>One console line: the wall-clock time it was written, its severity and its text.</summary>
    public sealed record ConsoleLogEntry(DateTime Timestamp, LogLevel Level, string Message)
    {
        /// <summary>
        /// Monotonic id assigned by the logger. Views use it to tell a line they already hold (from
        /// the backlog) apart from a new one, since the same text can legitimately repeat.
        /// </summary>
        public long Sequence { get; init; }

        public string TimestampText => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        public string LevelText => Level switch
        {
            LogLevel.Success => "SUCCESS",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            LogLevel.Process => "PROCESS",
            _ => "INFO"
        };

        /// <summary>Plain text form used by copy-to-clipboard exports and the log file.</summary>
        public override string ToString() => $"[{TimestampText}] [{LevelText}] {Message}";
    }

    public interface IConsoleLogger
    {
        /// <summary>Raised on the UI thread with every batch of lines that arrived since the last flush.</summary>
        event EventHandler<IReadOnlyList<ConsoleLogEntry>>? EntriesFlushed;

        /// <summary>The tail of the stream, so a console opened late still has context.</summary>
        IReadOnlyList<ConsoleLogEntry> Backlog { get; }

        void Write(LogLevel level, string message);
    }

    public static class ConsoleLoggerExtensions
    {
        public static void Info(this IConsoleLogger logger, string message) => logger.Write(LogLevel.Info, message);

        public static void Success(this IConsoleLogger logger, string message) => logger.Write(LogLevel.Success, message);

        public static void Warn(this IConsoleLogger logger, string message) => logger.Write(LogLevel.Warning, message);

        public static void Error(this IConsoleLogger logger, string message) => logger.Write(LogLevel.Error, message);

        public static void Process(this IConsoleLogger logger, string message) => logger.Write(LogLevel.Process, message);
    }

    /// <summary>
    /// Console sink for the whole desktop application.
    /// Producers (device threads, process streams, API calls) never touch the UI: they write into a
    /// bounded channel, a single consumer drains it in batches and marshals one event per batch to
    /// the dispatcher. Heavy adb/fastboot output therefore cannot flood or freeze the interface.
    /// </summary>
    public sealed class ConsoleLoggerService : IConsoleLogger, IDisposable
    {
        private const int ChannelCapacity = 10_000;
        private const int MaxBatch = 256;
        private const int BacklogCapacity = 500;

        private readonly Channel<ConsoleLogEntry> _channel = Channel.CreateBounded<ConsoleLogEntry>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        private readonly ConcurrentQueue<ConsoleLogEntry> _backlog = new();
        private readonly Dispatcher? _dispatcher;
        private readonly CancellationTokenSource _shutdown = new();
        private long _sequence;
        private readonly Task _pump;
        private bool _disposed;

        public ConsoleLoggerService(Dispatcher? dispatcher = null)
        {
            _dispatcher = dispatcher;
            _pump = Task.Run(PumpAsync);
        }

        public event EventHandler<IReadOnlyList<ConsoleLogEntry>>? EntriesFlushed;

        public IReadOnlyList<ConsoleLogEntry> Backlog => _backlog.ToArray();

        public void Write(LogLevel level, string message)
        {
            if (_disposed || string.IsNullOrEmpty(message))
            {
                return;
            }

            ConsoleLogEntry entry = new(DateTime.Now, level, message)
            {
                Sequence = Interlocked.Increment(ref _sequence)
            };

            _backlog.Enqueue(entry);
            while (_backlog.Count > BacklogCapacity && _backlog.TryDequeue(out _))
            {
                // The backlog is a window on recent history, not a growing archive.
            }

            _channel.Writer.TryWrite(entry);
        }

        private async Task PumpAsync()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    List<ConsoleLogEntry> batch = new(64);
                    while (batch.Count < MaxBatch && _channel.Reader.TryRead(out ConsoleLogEntry? entry))
                    {
                        batch.Add(entry);
                    }

                    if (batch.Count > 0)
                    {
                        Publish(batch);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        }

        private void Publish(IReadOnlyList<ConsoleLogEntry> batch)
        {
            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                EntriesFlushed?.Invoke(this, batch);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => EntriesFlushed?.Invoke(this, batch)));
            }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
            {
                // The window is closing; dropping the last batch is harmless.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _channel.Writer.TryComplete();
            _shutdown.Cancel();

            try
            {
                _pump.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The consumer was already gone.
            }

            _shutdown.Dispose();
        }
    }
}
