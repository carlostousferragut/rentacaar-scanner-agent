using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace RentaCaaR.ScannerAgent.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly string _logFilePrefix;
    private readonly string _logFileExtension;
    private readonly LogLevel _minLevel;
    private readonly int _retentionDays;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private DateOnly _lastCleanupDate = DateOnly.MinValue;

    public FileLoggerProvider(string logFilePath, LogLevel minLevel, int retentionDays)
    {
        _minLevel = minLevel;
        _retentionDays = Math.Max(1, retentionDays);

        _logDirectory = Path.GetDirectoryName(logFilePath)
            ?? Path.Combine(Environment.CurrentDirectory, "logs");

        var fileName = Path.GetFileNameWithoutExtension(logFilePath);
        _logFilePrefix = string.IsNullOrWhiteSpace(fileName) ? "agent" : fileName;

        var ext = Path.GetExtension(logFilePath);
        _logFileExtension = string.IsNullOrWhiteSpace(ext) ? ".log" : ext;

        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    internal void WriteLine(string line)
    {
        lock (_sync)
        {
            CleanupOldLogsIfNeeded();
            File.AppendAllText(GetCurrentLogFilePath(), line + Environment.NewLine);
        }
    }

    public void Dispose() { }

    private string GetCurrentLogFilePath()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var fileName = $"{_logFilePrefix}-{today}{_logFileExtension}";
        return Path.Combine(_logDirectory, fileName);
    }

    private void CleanupOldLogsIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _lastCleanupDate)
            return;

        _lastCleanupDate = today;
        var threshold = DateTime.Now.AddDays(-_retentionDays);
        var pattern = $"{_logFilePrefix}-*{_logFileExtension}";

        foreach (var file in Directory.GetFiles(_logDirectory, pattern))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < threshold)
                    info.Delete();
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string category, FileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line =
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_category}: {formatter(state, exception)}";

            if (exception != null)
                line += $"{Environment.NewLine}{exception}";

            _provider.WriteLine(line);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
