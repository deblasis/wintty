using System;
using System.Security.Cryptography;
using System.Text;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// Derives the named-mutex and named-pipe identifiers used to coordinate
/// single-instance mode. Keyed off the executable path so two different
/// installs never collide, while two launches of the SAME install share
/// one identity. The mutex is <c>Local\</c>-scoped: single-instance is a
/// per-user-session notion, matching "one instance for me", and avoids
/// the cross-session / elevation pitfalls of a <c>Global\</c> object.
/// Pure (no I/O) so it is unit-testable.
/// </summary>
public static class SingleInstanceNames
{
    public readonly record struct Names(string Mutex, string Pipe);

    public static Names For(string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);

        // Normalize so case / separator differences map to one identity:
        // Windows paths are case-insensitive and Zig hands us forward
        // slashes in places.
        var normalized = exePath.Replace('/', '\\').ToLowerInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        // 16 hex chars (64 bits) is plenty to avoid collisions between
        // installs and keeps the names short.
        var token = Convert.ToHexString(hashBytes, 0, 8).ToLowerInvariant();

        return new Names(
            Mutex: $@"Local\Wintty-SingleInstance-{token}",
            Pipe: $"wintty-single-instance-{token}");
    }
}
