using System;
using System.IO;

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
public static class TestConfigGuard
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
    /// Whether <paramref name="path"/> names something inside the user's
    /// temp directory (<see cref="Path.GetTempPath"/>), after both are
    /// normalized. The temp directory itself counts; a sibling that shares
    /// its prefix does not.
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

        var temp = Normalize(Path.GetTempPath());
        return full.StartsWith(temp + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               || full.Equals(temp, StringComparison.OrdinalIgnoreCase);
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
            $"because it is not under '{Path.GetTempPath()}'. Point " +
            $"XDG_CONFIG_HOME at a directory under the temp directory " +
            $"(windows/scripts/lib/test-config.ps1 does this) and relaunch.");
    }

    /// <summary>
    /// Absolute, separator-normalized, trailing-separator-trimmed form of
    /// the path, with any <c>\\?\</c> or <c>\\.\</c> device prefix removed
    /// first so the prefix cannot smuggle a path past a textual compare.
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
}
