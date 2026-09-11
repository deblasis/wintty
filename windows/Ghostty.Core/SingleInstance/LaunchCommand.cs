using System;
using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// The command half of a forwarded launch (#1094): what the primary must
/// run in the new window's first pane when the secondary was started as
/// <c>wintty -e &lt;command...&gt;</c>. A cold start turns exactly that into
/// the first surface's command (libghostty's Config.parseManuallyHook:
/// the first <c>-e</c> on the command line consumes everything after it as
/// a direct command), so the primary honours the same argv rather than
/// degrading it to the default shell. Positional arguments are not a cold
/// start's command surface (libghostty's arg parser rejects them), so they
/// are not one here either; config flags never arrive, because they force
/// the launch into its own process before any forward happens.
///
/// The command is rendered as ONE shell-form string, because that is the
/// seam the shell hands a pane's command through (the profile
/// snapshot's <c>ResolvedCommand</c>, which the surface config passes to
/// libghostty as its per-surface command). On Windows libghostty splits a
/// shell-form command with a CommandLineToArgvW-compatible iterator and
/// spawns it directly (cmd.exe only when the string needs cmd's
/// metacharacters), so each part is quoted with those same rules and the
/// string re-parses to the argv that was forwarded.
///
/// Pure (no I/O) so the model is unit-testable without a GUI.
/// </summary>
public static class LaunchCommand
{
    /// <summary>
    /// The flag that starts a command, spelled exactly as libghostty's
    /// parser matches it (no <c>--e</c>, no <c>/e</c>).
    /// </summary>
    public const string ExecuteFlag = "-e";

    /// <summary>
    /// The command <paramref name="args"/> carry, or null when they carry
    /// none. Everything after the first <paramref name="ExecuteFlag"/> is
    /// the command, mirroring the cold-start parser; a flag with nothing
    /// after it is no command (a cold start diagnoses it and still opens
    /// a window, so a forward opens the default window too).
    /// </summary>
    public static string? FromArgs(IReadOnlyList<string>? args)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], ExecuteFlag, StringComparison.Ordinal))
                continue;

            if (i + 1 >= args.Count) return null;
            return JoinCommand(args, i + 1);
        }

        return null;
    }

    /// <summary>
    /// Join argv parts into one shell-form command string, quoting each
    /// part the way the Windows command-line rules require so the native
    /// side's split reproduces the parts that were forwarded.
    /// </summary>
    internal static string JoinCommand(IReadOnlyList<string> parts, int start)
    {
        var sb = new StringBuilder();
        for (var i = start; i < parts.Count; i++)
        {
            if (i > start) sb.Append(' ');
            AppendPart(sb, parts[i]);
        }
        return sb.ToString();
    }

    // CommandLineToArgvW round-trip quoting: wrap the part in quotes when
    // it contains whitespace or a quote (or is empty), double the
    // backslashes that immediately precede a quote or the closing quote,
    // and escape embedded quotes with a backslash. Anything else passes
    // through untouched, which is also what keeps the common case
    // (`pwsh -NoLogo`) readable in a profile snapshot.
    private static void AppendPart(StringBuilder sb, string part)
    {
        var plain = part.Length > 0;
        foreach (var ch in part)
        {
            if (ch is ' ' or '\t' or '"')
            {
                plain = false;
                break;
            }
        }
        if (plain)
        {
            sb.Append(part);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in part)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }
            backslashes = 0;
        }
        // The closing quote terminates a run of backslashes, so they double.
        sb.Append('\\', backslashes * 2).Append('"');
    }
}
