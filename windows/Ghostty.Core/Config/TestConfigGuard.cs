using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Config;

/// <summary>
/// The app-side half of the test-config isolation rule: when
/// <c>WINTTY_TEST_CONFIG</c> is armed, the app must not read, create, seed
/// or write any config file outside the user's temp directory.
///
/// The rule exists because config resolution is process-global and defaults
/// to the real per-user file: a harness that points a read at a temp file
/// still had every Settings-UI write land in the real config, which is how
/// manual test matrices ended up living inside it. <c>XDG_CONFIG_HOME</c>
/// under a per-run random temp directory moves every read and every writer
/// at once, and this guard turns "forgot to set it" from a silent write
/// into a loud startup refusal.
///
/// "Temp" means the canonical known-folder temp
/// (<c>%LOCALAPPDATA%\Temp</c> resolved through the known-folder API, which
/// the process environment cannot move), never <see cref="Path.GetTempPath"/>,
/// which follows TMP/TEMP - the same actor the guard polices. When armed,
/// a TEMP or TMP that points outside the anchor is itself refused, so a
/// redirected environment cannot quietly move the reference point (M1).
///
/// The boundary compare runs on final paths: reparse points (junctions,
/// symlinks) anywhere between the anchor and the config path are resolved
/// with GetFinalPathNameByHandle before the compare, so a junction inside
/// temp cannot deliver a write outside it while the guard reads green (M4).
///
/// The two spellings the guard must never accept:
/// <list type="bullet">
/// <item>a root that merely shares a prefix with the temp directory
/// (<c>C:\...\Temp-evil</c>): the separator check in <see cref="IsUnderTemp"/>
/// is what refuses it;</item>
/// <item>a device-prefixed path (<c>\\?\C:\...</c>): the prefix survives
/// <see cref="Path.GetFullPath"/> and would dodge an Ordinal compare, so it
/// is stripped first.</item>
/// </list>
///
/// Checks are plain path comparisons over values already in hand, so this
/// class is unit-testable without libghostty, WinUI or a window.
/// </summary>
public static partial class TestConfigGuard
{
    /// <summary>
    /// The env var that arms the guard. Set it to <c>1</c> (the spelling
    /// every harness in this repo uses). An unset, empty, <c>0</c> or
    /// <c>false</c> value leaves the app's behaviour completely unchanged:
    /// a user's install never sets it and must never notice it exists.
    /// </summary>
    public const string EnvVar = "WINTTY_TEST_CONFIG";

    /// <summary>
    /// Whether the guard is armed. Read from the environment on every call
    /// rather than cached: the decision belongs to whoever launched this
    /// process, and a test flipping the variable between calls is the
    /// cheapest red/green pair there is.
    /// </summary>
    public static bool IsArmed
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrEmpty(value)
                   && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
                   && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The canonical temp anchor: <c>%LOCALAPPDATA%\Temp</c> from the
    /// known-folder API, which reads profile state, not the process
    /// environment. <see cref="Path.GetTempPath"/> is deliberately NOT
    /// used: it follows TMP/TEMP, and a harness (or CI quirk) that points
    /// those at an ancestor of the real config would move the guard's own
    /// reference point. Empty means the anchor could not be established
    /// (no profile), and the guard then refuses everything: fail-closed.
    /// </summary>
    public static string TempAnchor
    {
        get
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData)) return string.Empty;
            return Path.Combine(localAppData, "Temp");
        }
    }

    /// <summary>
    /// The config root the native side will resolve, mirroring
    /// <c>src/os/xdg.zig dir()</c> for the config dir, including its
    /// set-but-empty handling: a set-but-empty XDG_CONFIG_HOME does NOT
    /// fall through to APPDATA (zig's <c>orelse</c> sees Some("")), it
    /// falls to the home + <c>.config</c> default. Pinned together with
    /// the zig source by <c>XdgRootMirrorParityTests</c>.
    /// </summary>
    public static string ResolveConfigRoot()
    {
        var pick = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (pick is null) pick = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(pick))
        {
            pick = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }
        return pick;
    }

    /// <summary>
    /// Whether <paramref name="path"/> names something inside the canonical
    /// temp directory, after normalization AND final-path resolution. The
    /// anchor directory itself counts; a sibling that shares its prefix
    /// does not; a reparse point that leaves the tree does not.
    /// </summary>
    public static bool IsUnderTemp(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or PathTooLongException or
            NotSupportedException or System.Security.SecurityException)
        {
            // A path that cannot be normalized cannot be proven safe, and
            // the guard's failure mode is to refuse, not to wave through.
            return false;
        }

        var anchor = ResolvedAnchor;
        if (anchor.Length == 0) return false;

        var resolved = ResolveFinal(full);
        if (resolved is null) return false;
        return resolved.StartsWith(anchor + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               || resolved.Equals(anchor, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The anchor itself is resolved once and cached: it is a fixed
    /// profile location, and every check pays for the handle open.
    /// </summary>
    private static readonly Lazy<string> _resolvedAnchor = new(() =>
    {
        var anchor = TempAnchor;
        if (anchor.Length == 0) return string.Empty;
        try
        {
            return Normalize(ResolveFinal(Normalize(anchor)));
        }
        catch (Exception ex) when (
            ex is ArgumentException or PathTooLongException or
            NotSupportedException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    });

    private static string ResolvedAnchor => _resolvedAnchor.Value;

    /// <summary>
    /// Refuse loudly when armed and TEMP or TMP points outside the
    /// canonical temp tree. Those variables are exactly how the guard's
    /// reference point would be moved, so an armed run with a redirected
    /// scratch TEMP is told to put its scratch somewhere under the real
    /// temp directory instead.
    /// </summary>
    public static void AssertTempEnvironmentIntact()
    {
        if (!IsArmed) return;
        foreach (var name in new[] { "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) continue;
            if (IsUnderTemp(value)) continue;

            throw new InvalidOperationException(
                $"{EnvVar} is set, so {name} must point inside the canonical " +
                $"temp directory '{TempAnchor}'. Refusing to run with " +
                $"{name}='{value}': the temp anchor follows TMP/TEMP, and a " +
                $"redirected one would move what the guard treats as temp. " +
                $"Put the run's scratch under the real temp directory.");
        }
    }

    /// <summary>
    /// Refuse loudly when armed and <paramref name="path"/> is outside the
    /// temp directory. The message names both paths and the env var, because
    /// the reader of the failure is whoever wrote the harness: the fix is on
    /// their side of the launch, not inside the app.
    /// </summary>
    /// <param name="path">The config path about to be read or written.</param>
    /// <param name="what">What the caller was about to do with it, e.g.
    /// "resolved config path" or "config write". Appears in the message.</param>
    /// <exception cref="InvalidOperationException">
    /// <see cref="IsArmed"/> and <paramref name="path"/> failed
    /// <see cref="IsUnderTemp"/>.
    /// </exception>
    public static void AssertUnderTemp(string path, string what)
    {
        if (!IsArmed) return;
        if (IsUnderTemp(path)) return;

        throw new InvalidOperationException(
            $"{EnvVar} is set, so this process may not touch a config file " +
            $"outside the temp directory. Refused {what} against '{path}' " +
            $"because it is not under '{TempAnchor}'. Point " +
            $"XDG_CONFIG_HOME at a directory under the temp directory " +
            $"(windows/scripts/lib/test-config.ps1 does this) and relaunch.");
    }

    /// <summary>
    /// Absolute, separator-normalized, trailing-separator-trimmed form of
    /// the path, with any <c>\\?\</c> or <c>\\.\</c> device prefix removed
    /// first so the prefix cannot smuggle a path past a textual compare.
    /// Reparse points are NOT resolved here; that is
    /// <see cref="ResolveFinal"/>'s job.
    /// </summary>
    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.Substring(4);
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
            path = path.Substring(4);

        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // ---- final-path resolution (M4) -----------------------------------
    //
    // GetFullPath leaves reparse points in place, so a junction inside the
    // temp tree compares as "inside" while the bytes it carries land
    // wherever the junction points. GetFinalPathNameByHandle answers with
    // the path the object system actually resolved, reparse points and all.

    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [LibraryImport("kernel32.dll", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    // An unmanaged buffer rather than StringBuilder: this assembly disables
    // runtime marshalling, so the call goes through source-generated
    // marshalling with a blittable IntPtr.
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
    private static partial uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        IntPtr lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// The final path of <paramref name="fullPath"/>: the longest existing
    /// ancestor is opened and resolved through the object manager (which is
    /// what makes junctions and symlinks visible), and any not-yet-existing
    /// tail is appended below it. A tail cannot hide a reparse point: it
    /// does not exist yet, and by the time it does, it is part of the
    /// existing prefix the next check resolves. An existing path whose
    /// handle cannot be opened resolves to null, and the caller refuses:
    /// the guard's failure mode is to refuse, not to wave through.
    /// </summary>
    internal static string? ResolveFinal(string fullPath)
    {
        // Walk down until an ancestor exists. The root itself counts as a
        // file or a directory; either opens with BACKUP_SEMANTICS.
        var existing = fullPath;
        var tail = string.Empty;
        while (existing.Length > 2)
        {
            if (Directory.Exists(existing) || File.Exists(existing)) break;
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent)) return null;
            tail = string.IsNullOrEmpty(tail)
                ? Path.GetFileName(existing)
                : Path.Combine(Path.GetFileName(existing), tail);
            existing = parent.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var handle = CreateFileW(
            existing, dwDesiredAccess: 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return null;

        try
        {
            var capacity = 1024u;
            var buffer = Marshal.AllocHGlobal((int)(capacity * 2));
            try
            {
                var length = GetFinalPathNameByHandleW(handle, buffer, capacity, 0);
                if (length == 0) return null;
                if (length > capacity)
                {
                    Marshal.FreeHGlobal(buffer);
                    capacity = length;
                    buffer = Marshal.AllocHGlobal((int)(capacity * 2));
                    length = GetFinalPathNameByHandleW(handle, buffer, capacity, 0);
                    if (length == 0) return null;
                }

                var final = Marshal.PtrToStringUni(buffer, (int)length)!;
                if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    final = @"\\" + final.Substring(8);
                else if (final.StartsWith(@"\\?\", StringComparison.Ordinal))
                    final = final.Substring(4);

                return string.IsNullOrEmpty(tail)
                    ? final.TrimEnd(Path.DirectorySeparatorChar,
                          Path.AltDirectorySeparatorChar)
                    : final.TrimEnd(Path.DirectorySeparatorChar,
                          Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar + tail;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
