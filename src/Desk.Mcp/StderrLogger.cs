using Microsoft.Extensions.Logging;

namespace Desk.Mcp;

/// <summary>
/// Logs to a writer that is stderr in production: stdout carries only the protocol (ADR-0031 §1). Every line goes through
/// <see cref="Redact"/>, so a token or email that reached a message or an exception still never prints.
/// </summary>
internal sealed class StderrLoggerProvider(TextWriter stderr, LogLevel minimum = LogLevel.Information) : ILoggerProvider
{
    private readonly Lock _write = new();
    private readonly TextWriter _stderr = stderr;
    private readonly LogLevel _minimum = minimum;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose() { }

    private sealed class Logger(StderrLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"desk-mcp {logLevel.ToString().ToLowerInvariant()} {category}: {formatter(state, exception)}";
            if (exception is not null) line += $" ({exception.GetType().Name}: {exception.Message})";
            lock (owner._write)
            {
                owner._stderr.WriteLine(Redact.Text(line));
                owner._stderr.Flush();
            }
        }
    }
}
