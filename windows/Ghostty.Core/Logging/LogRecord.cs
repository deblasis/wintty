using System;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Logging;

/// <summary>
/// Record carried through <see cref="FileLoggerProvider"/>'s channel.
/// Kept as a readonly struct so the producer path is allocation-free
/// except for the string message the ILogger formatter produces.
/// </summary>
internal readonly record struct LogRecord(
    DateTime Timestamp,
    LogLevel Level,
    EventId EventId,
    string Category,
    string Message,
    Exception? Exception);
