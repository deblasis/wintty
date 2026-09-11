using System;
using System.Security.Cryptography;
using System.Text;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// Derives the named-mutex and named-pipe identifiers used to coordinate
/// single-instance mode. One identity means one edition of one install:
/// the edition id (<see cref="AppIdentity.AumId"/>, baked per tier and
/// variant so Wintty, Wintty Pro and Pro's own variants each carry their
/// own) keeps sideloaded editions separate apps that run side by side,
/// while the executable path keeps two installs of the SAME edition (a
/// dev tree beside an installed app) from handing a dev launch to the
/// installed primary. Under <c>WINTTY_TEST_CONFIG</c> the resolved config
/// root joins the material, which is what keeps a harness launch off every
/// non-test instance and off every other harness's random root (#1094).
///
/// The mutex is <c>Local\</c>-scoped: single-instance is a per-user-session
/// notion, matching "one instance for me", and avoids the cross-session /
/// elevation pitfalls of a <c>Global\</c> object.
/// Pure (no I/O) so it is unit-testable; <see cref="ForProcess"/> is the
/// composition production code reaches for.
/// </summary>
public static class SingleInstanceNames
{
    public readonly record struct Names(string Mutex, string Pipe);

    /// <summary>
    /// The identity for a launch of <paramref name="exePath"/> in edition
    /// <paramref name="editionId"/>, scoped to <paramref name="testConfigRoot"/>
    /// when the caller carries one (<see cref="ForProcess"/> passes the
    /// resolved config root under the test marker, null otherwise).
    /// </summary>
    public static Names For(string exePath, string editionId, string? testConfigRoot)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(editionId);

        // Normalize so case / separator differences map to one identity:
        // Windows paths are case-insensitive and Zig hands us forward
        // slashes in places.
        var normalizedPath = exePath.Replace('/', '\\').ToLowerInvariant();
        var normalizedEdition = editionId.Trim().ToLowerInvariant();
        var normalizedRoot = testConfigRoot?
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();

        // '\0' cannot occur in a path or an AUMID, so the fields cannot
        // bleed into one another: an edition id ending where a path begins
        // is a different material than any honest spelling of either.
        var material = string.Join(
            "\0", normalizedPath, normalizedEdition, normalizedRoot ?? string.Empty);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        // 16 hex chars (64 bits) is plenty to avoid collisions between
        // identities and keeps the names short.
        var token = Convert.ToHexString(hashBytes, 0, 8).ToLowerInvariant();

        return new Names(
            Mutex: $@"Local\Wintty-SingleInstance-{token}",
            Pipe: $"wintty-single-instance-{token}");
    }

    /// <summary>
    /// The composition a real process elects under: the edition from
    /// <see cref="AppIdentity.AumId"/> (the pack id reaches it as a build-time
    /// override of <c>_WinttyAumId</c>, which is the only per-variant input a
    /// tier build carries), and the test scope from the #1084 guard's own
    /// root resolution, so an armed launch never lands on the election of a
    /// process that is not under the marker.
    /// </summary>
    public static Names ForProcess(string exePath) =>
        For(
            exePath,
            AppIdentity.AumId,
            Ghostty.Core.Config.TestConfigGuard.IsArmed
                ? Ghostty.Core.Config.TestConfigGuard.ResolveConfigRoot()
                : null);
}
