using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace RentaCaaR.ScannerAgent.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;
    private readonly LogLevel _minLevel;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string logFilePath, LogLevel minLevel)
    {
        _logFilePath = logFilePath;
        _minLevel = minLevel;

        var dir = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _logFilePath, _minLevel, _sync));

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly string _path;
        private readonly LogLevel _minLevel;
        private readonly object _sync;

        public FileLogger(string category, string path, LogLevel minLevel, object sync)
        {
            _category = category;
            _path = path;
            _minLevel = minLevel;
            _sync = sync;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

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

            lock (_sync)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
