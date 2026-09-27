using System;

namespace Ghostty.Core.Logging;

/// <summary>
/// The one greppable line naming why the process is about to exit
/// (wintty#967). Before this existed, every
/// <c>Environment.Exit(0)</c> site - help, +version, a forwarded
/// list-themes, a forwarded launch, a CLI action completing - produced an
/// exit event with nothing attributing it, so a clean-looking close could
/// not be told apart from any other.
///
/// stderr is the channel: on CLI paths it is the terminal the user is
/// watching (or gpu.log under the WINTTY_GPU_LOG opt-in), and on the GUI
/// path the primary instance's stderr is gpu.log. The one GUI exit site -
/// the forwarded second launch - is the exception worth stating rather
/// than hiding: its own redirect has normally already failed by then (the
/// live primary holds gpu.log against writers), so the line reaches a
/// terminal when the launch has one, and a double-click launch has
/// neither console nor writable gpu.log. One line, one channel, no logger
/// machinery on paths that run before the logger exists.
/// </summary>
internal static class ExitReason
{
    /// <summary>The exact line a caller writes, exposed as a seam: the
    /// format is the contract support tooling greps for, and a pure
    /// function is testable without swapping the process-global
    /// Console.Error under a parallel test host. One line is part of that
    /// contract, so a reason carrying a newline (a CLI action named by
    /// user input) has it flattened - a forged second line would be
    /// read as a second exit record.</summary>
    public static string Line(int code, string why)
    {
        var clean = why?.Replace('\r', ' ').Replace('\n', ' ');
        return $"{AppIdentity.LogTag} exit {code}: {clean}";
    }

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
