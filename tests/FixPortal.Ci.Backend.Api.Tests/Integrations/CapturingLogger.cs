using Microsoft.Extensions.Logging;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

/// <summary>
/// Records formatted log messages so tests can assert on operator-facing output —
/// e.g. that a one-time permission warning fires once, clears on recovery, and fires
/// again on a regression — instead of reaching into client internals. Sequential
/// callers only; there is no locking.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (logLevel >= LogLevel.Information)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
