using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IIoT.Integration.Tests.Infrastructure;

/// <summary>
/// Keeps every log entry from every category, formatted as it would be written, so a test can assert
/// on what a component logged when it was composed by dependency injection rather than by hand.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    /// <summary>The entries of one category, which is the logging class's full name.</summary>
    public IReadOnlyList<CapturedLog> For<T>() => [.. Entries.Where(entry => entry.Category == typeof(T).FullName)];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
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

            entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception, properties));
        }
    }
}

public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties);
