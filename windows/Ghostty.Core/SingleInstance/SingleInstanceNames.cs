using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// Derives the named-mutex and named-pipe identifiers used to coordinate
/// single-instance mode. One identity means one edition of one install in
/// one terminal-services session: the edition id (<see cref="AppIdentity.AumId"/>,
/// baked per tier and variant so Wintty, Wintty Pro and Pro's own variants
/// each carry their own) keeps sideloaded editions separate apps that run
/// side by side, while the executable path keeps two installs of the SAME
/// edition (a dev tree beside an installed app) from handing a dev launch
/// to the installed primary. The resolved config root is always material:
/// under <c>WINTTY_TEST_CONFIG</c> that is what keeps a harness launch off
/// every non-test instance and off every other harness's random root
/// (#1094), and without the marker it keeps a misconfigured harness (a
/// redirected XDG_CONFIG_HOME and no marker) from forwarding into the real
/// instance. The marker itself is material too, so an armed and an unarmed
/// launch never share an election however their roots are spelled. The
/// session id reaches BOTH names because the two namespaces disagree:
/// <c>Local\</c> objects are per session, named pipes are per machine, and
/// the same user's second session must not forward its launches into the
/// first session's pipe (#1094 review M1).
///
/// The mutex stays <c>Local\</c>-scoped: single-instance is a
/// per-user-session notion, matching "one instance for me", and avoids the
/// cross-session / elevation pitfalls of a <c>Global\</c> object.
/// Pure (no I/O) so it is unit-testable; <see cref="ForProcess"/> is the
/// composition production code reaches for.
/// </summary>
public static partial class SingleInstanceNames
{
    public readonly record struct Names(string Mutex, string Pipe);

    /// <summary>
    /// The identity for a launch of <paramref name="exePath"/> in edition
    /// <paramref name="editionId"/> at config root <paramref name="configRoot"/>
    /// (the value <see cref="TestConfigGuard.ResolveConfigRoot"/> answers,
    /// null for none), under test marker <paramref name="testMarker"/>, in
    /// terminal-services session <paramref name="sessionId"/>.
    /// </summary>
    public static Names For(
        string exePath,
        string editionId,
        string? configRoot,
        bool testMarker,
        uint sessionId)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(editionId);

        // Normalize so case / separator differences map to one identity:
        // Windows paths are case-insensitive and Zig hands us forward
        // slashes in places. The root gets prefix stripping and
        // GetFullPath as well, so a \\?\ spelling or a relative spelling
        // hashes like the root it names (resolved against the working
        // directory, which is the directory the launch would read).
        var normalizedPath = NormalizePath(exePath);
        var normalizedEdition = editionId.Trim().ToLowerInvariant();
        var normalizedRoot = NormalizeRoot(configRoot);

        // '\0' cannot occur in a path or an AUMID, so the fields cannot
        // bleed into one another: an edition id ending where a path begins
        // is a different material than any honest spelling of either.
        var material = string.Join(
            "\0",
            normalizedPath,
            normalizedEdition,
            testMarker ? "WINTTY_TEST_CONFIG" : string.Empty,
            normalizedRoot,
            sessionId.ToString(CultureInfo.InvariantCulture));
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
    /// <see cref="AppIdentity.AumId"/> (the pack id reaches it as a
    /// build-time override of <c>_WinttyAumId</c>, which is the only
    /// per-variant input a tier build carries), the config root and marker
    /// from the #1084 guard's own resolution, and this process's
    /// terminal-services session.
    /// </summary>
    public static Names ForProcess(string exePath) =>
        For(
            exePath,
            AppIdentity.AumId,
            Ghostty.Core.Config.TestConfigGuard.ResolveConfigRoot(),
            Ghostty.Core.Config.TestConfigGuard.IsArmed,
            ReadSessionId());

    /// <summary>
    /// The terminal-services session this process runs in. INTERNAL seam
    /// with a live default, the same shape as the test-config guard's env
    /// reader: tests inject the session ids of two different logon
    /// sessions, production reads the real one. A failed read answers 0,
    /// which every launch then shares: that is today's cross-session
    /// behaviour, not a new failure.
    /// </summary>
    internal static Func<uint> ReadSessionId { get; set; } = ReadCurrentSessionId;

    private static uint ReadCurrentSessionId() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out var session)
            ? session
            : 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').ToLowerInvariant();

    /// <summary>
    /// Absolute, separator-normalized, prefix-stripped form of a config
    /// root, or the empty string for null. An unresolvable spelling (a
    /// path the runtime refuses to make absolute) is hashed as-is: two
    /// processes that cannot agree on an absolute form then share nothing,
    /// which fails open, the direction that costs a window nobody loses.
    /// </summary>
    private static string NormalizeRoot(string? root)
    {
        if (string.IsNullOrEmpty(root)) return string.Empty;

        var stripped = root;
        if (stripped.StartsWith(@"\\?\", StringComparison.Ordinal))
            stripped = stripped[4..];
        else if (stripped.StartsWith(@"\\.\", StringComparison.Ordinal))
            stripped = stripped[4..];

        try
        {
            stripped = Path.GetFullPath(stripped);
        }
        catch (Exception ex) when (
            ex is ArgumentException or PathTooLongException or
            NotSupportedException or System.Security.SecurityException)
        {
            // Hash the raw spelling; see the remark above.
        }

        return stripped.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }
}
