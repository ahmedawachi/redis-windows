using Microsoft.Extensions.Logging;

namespace RedisService.Service;

/// <summary>
/// Console logger for foreground mode: wrapper events as "[time] LEVEL message", Redis output as the raw line.
/// Written by hand because it is tiny and has no configuration binding (keeps trimming warning-free).
/// </summary>
public sealed class ConsoleLineLoggerProvider(TextWriter output, TextWriter error, LogLevel minimumLevel) : ILoggerProvider
{
    private readonly Lock _lock = new();

    public ILogger CreateLogger(string categoryName) => new ConsoleLineLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_lock)
        {
            if (category == "RedisService.RedisOutput")
            {
                (level >= LogLevel.Warning ? error : output).WriteLine(message);
                return;
            }
            var label = level switch
            {
                LogLevel.Critical => "CRIT",
                LogLevel.Error => "ERROR",
                LogLevel.Warning => "WARN",
                LogLevel.Information => "INFO",
                _ => "DEBUG",
            };
            var writer = level >= LogLevel.Warning ? error : output;
            writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {label} {message}");
            if (exception is not null) writer.WriteLine(exception);
        }
    }

    private sealed class ConsoleLineLogger(ConsoleLineLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= (category == "RedisService.RedisOutput" ? LogLevel.Debug : provider.Minimum);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }

    private LogLevel Minimum => minimumLevel;
}
