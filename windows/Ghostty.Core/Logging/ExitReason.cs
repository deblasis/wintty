using System;

namespace Ghostty.Core.Logging;

/// <summary>
/// The one greppable line naming why the process is about to exit
/// (wintty#967, tracker row D13). Before this existed, every
/// <c>Environment.Exit(0)</c> site - help, +version, a forwarded
/// list-themes, a forwarded launch, a CLI action completing - produced an
/// exit event with nothing attributing it, so a clean-looking close could
/// not be told apart from any other.
///
/// stderr is the channel on purpose: on the GUI path stderr is already
/// the gpu.log file (the redirect runs before the app starts), and on CLI
/// paths it is the terminal the user is watching. One line, one channel,
/// no logger machinery on paths that run before the logger exists.
/// </summary>
internal static class ExitReason
{
    /// <summary>The exact line a caller writes, exposed as a seam: the
    /// format is the contract support tooling greps for, and a pure
    /// function is testable without swapping the process-global
    /// Console.Error under a parallel test host.</summary>
    public static string Line(int code, string why) =>
        $"{AppIdentity.LogTag} exit {code}: {why}";

    /// <summary>
    /// Write the exit line. Best-effort by contract: the reason must never
    /// change the exit, so a stderr handle that refuses the write (a
    /// detached process, a closed pipe) is swallowed - the exit itself is
    /// the caller's next statement and proceeds regardless.
    /// </summary>
    public static void Log(int code, string why)
    {
        try
        {
            Console.Error.WriteLine(Line(code, why));
        }
        catch (Exception)
        {
            // Deliberate: see the class doc comment. Losing the reason line
            // is the fallback; throwing from here is not an option.
        }
    }
}
