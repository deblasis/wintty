using System;

namespace Ghostty.Core.Logging;

/// <summary>
/// Minimal UTC clock abstraction so day-rollover and retention tests
/// can control time. Production impl is <see cref="SystemClock"/>.
/// Returns <see cref="DateTimeOffset"/> so callers that persist cache
/// metadata (e.g. Profiles.DiscoveryCacheFile.CreatedAt) round-trip
/// losslessly.
/// </summary>
internal interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly UtcToday => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}
