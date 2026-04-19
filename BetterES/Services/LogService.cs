using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;

namespace BetterES.Services
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error,
        Debug
    }

    public class LogEntry
    {
        public int Id { get; init; }
        public DateTime Timestamp { get; init; }
        public LogLevel Level { get; init; }
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";

        /// <summary>Formatted like a real log line: [HH:mm:ss.fff] [LEVEL] [Source] Message</summary>
        public string Formatted =>
            $"[{Timestamp:HH:mm:ss.fff}] [{Level,-7}] [{Source}] {Message}";
    }

    /// <summary>
    /// Central log aggregator. All services/pages push logs here.
    /// Writes to file + in-memory collection. Behaves like a normal log.
    /// </summary>
    public class LogService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private const int MaxEntries = 5000;
        private int _nextId;
        private readonly string _logFilePath;
        private readonly StreamWriter? _fileWriter;
        private readonly object _fileLock = new();

        public ObservableCollection<LogEntry> Entries { get; } = new();
        public event Action<LogEntry>? EntryAdded;

        public LogService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;

            // File logging — same folder as the app, one file per session
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"betteres_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

            try
            {
                _fileWriter = new StreamWriter(_logFilePath, append: false) { AutoFlush = true };
                _fileWriter.WriteLine($"# BetterES Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _fileWriter.WriteLine($"# Log file: {_logFilePath}");
                _fileWriter.WriteLine();
            }
            catch
            {
                // File logging is best-effort
            }
        }

        public void Info(string source, string message)  => Add(LogLevel.Info, source, message);
        public void Warn(string source, string message)   => Add(LogLevel.Warning, source, message);
        public void Error(string source, string message)  => Add(LogLevel.Error, source, message);
        public void Debug(string source, string message)  => Add(LogLevel.Debug, source, message);

        public void Add(LogLevel level, string source, string message)
        {
            var entry = new LogEntry
            {
                Id = System.Threading.Interlocked.Increment(ref _nextId),
                Timestamp = DateTime.Now,
                Level = level,
                Source = source,
                Message = message
            };

            // Write to file immediately
            lock (_fileLock)
            {
                try { _fileWriter?.WriteLine(entry.Formatted); }
                catch { /* best-effort */ }
            }

            // Add to UI collection on dispatcher thread
            _dispatcher.BeginInvoke(() =>
            {
                Entries.Add(entry);
                while (Entries.Count > MaxEntries)
                    Entries.RemoveAt(0);
            });

            EntryAdded?.Invoke(entry);
        }

        public void Clear()
        {
            _dispatcher.Invoke(() => Entries.Clear());
        }

        public void Dispose()
        {
            lock (_fileLock)
            {
                _fileWriter?.WriteLine();
                _fileWriter?.WriteLine($"# Log ended — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _fileWriter?.Dispose();
            }
        }
    }
}
