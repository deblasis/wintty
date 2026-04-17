using System;

namespace Ghostty.Core.Logging;

/// <summary>
/// Minimal UTC clock abstraction so day-rollover and retention tests
/// can control time. Production impl is <see cref="SystemClock"/>.
/// </summary>
internal interface IClock
{
    DateTime UtcNow { get; }
    DateOnly UtcToday => DateOnly.FromDateTime(UtcNow);
}
