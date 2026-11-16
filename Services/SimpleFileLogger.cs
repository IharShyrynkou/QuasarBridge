using System.Text;

namespace QuasarBridge.Services;

public sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly LogLevel _minLevel;

    public SimpleFileLoggerProvider(string path, LogLevel minLevel = LogLevel.Information)
    {
        _minLevel = minLevel;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(_writer, _lock, categoryName, _minLevel);

    public void Dispose()
    {
        lock (_lock) { _writer.Dispose(); }
    }

    private sealed class SimpleFileLogger : ILogger
    {
        private readonly StreamWriter _writer;
        private readonly object _lock;
        private readonly string _category;
        private readonly LogLevel _minLevel;

        public SimpleFileLogger(StreamWriter writer, object l, string category, LogLevel minLevel)
        {
            _writer = writer; _lock = l; _category = category; _minLevel = minLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            var line = $"{DateTime.Now:HH:mm:ss} {Short(logLevel)}: {_category}[{eventId.Id}] {msg}";
            if (exception != null) line += Environment.NewLine + exception;
            lock (_lock)
            {
                _writer.WriteLine(line);
            }
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "info"
        };
    }
}