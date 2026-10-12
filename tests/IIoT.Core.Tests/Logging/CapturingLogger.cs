using Microsoft.Extensions.Logging;

namespace IIoT.Core.Tests.Logging;

/// <summary>
/// Keeps every log entry, formatted as it would be written, so tests can assert both on what was
/// logged and on what must never be.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
            : [];

        lock (_gate)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, properties));
        }
    }
}

internal sealed record LogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties)
{
    /// <summary>Everything that would reach a log sink: the message and the exception text.</summary>
    public string Everything => $"{Message} {Exception}";
}
