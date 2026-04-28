using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Logging.Testing;

/// <summary>
/// In-memory <see cref="ILoggerProvider"/> that captures every emitted
/// entry. Used by contract tests to assert that a specific component
/// emits a specific <see cref="EventId"/> on a given code path.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedEntry> _entries = new();

    public IReadOnlyList<CapturedEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName)
        => new CapturingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<CapturedEntry> _sink;

        public CapturingLogger(string category, ConcurrentQueue<CapturedEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink.Enqueue(new CapturedEntry(
                _category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}

internal readonly record struct CapturedEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception);
