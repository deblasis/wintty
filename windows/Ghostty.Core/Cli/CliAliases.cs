using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.Cli;

/// <summary>
/// Windows-native subcommand spellings for the <c>+action</c> CLI
/// convention Wintty inherits from Ghostty.
///
/// `wintty +list-themes` is a Ghostty-ism. On Windows a verb is a bare
/// subcommand (`winget install`, `dotnet build`), so `wintty list-themes`
/// has to work too. Both forms are supported; neither is deprecated.
///
/// libghostty stays the authority on action detection. This type answers
/// only the one question libghostty cannot answer for itself - whether the
/// first argument is a bare alias that needs a <c>+</c> spliced in before
/// <c>ghostty_init_wide</c> parses the command line - and defers
/// everything else, including every error case, to
/// <c>src/cli/action.zig</c>.
///
/// Pure (no I/O, no P/Invoke) so it is unit-testable and so the drift
/// guard in Ghostty.Tests can exercise the real type.
/// </summary>
internal static class CliAliases
{
    /// <summary>
    /// Canonical action names, without the <c>+</c>. One entry per field
    /// of the <c>Action</c> enum in <c>src/cli/ghostty.zig</c>; a parity
    /// test fails the build when the two sets diverge.
    ///
    /// Kept in sorted order because <see cref="RenderHelp"/> prints it
    /// as-is, which is cheaper and more predictable than sorting a set
    /// whose enumeration order is unspecified. A test asserts the help
    /// output is ordered, so an entry added in the wrong place fails.
    /// </summary>
    private static readonly string[] Sorted =
    {
        "boo",
        "crash-report",
        "edit-config",
        "explain-config",
        "help",
        "list-actions",
        "list-colors",
        "list-fonts",
        "list-keybinds",
        "list-themes",
        "list-themes-tui",
        "new-tab",
        "new-window",
        "show-config",
        "show-face",
        "ssh",
        "ssh-cache",
        "toggle-quick-terminal",
        "validate-config",
        "version",
    };

    /// <summary>
    /// Lookup form of <see cref="Sorted"/>.
    ///
    /// Ordinal by construction, not by default: Ghostty.csproj sets
    /// InvariantGlobalization and Ghostty.Core.csproj does not, and that
    /// property is process-wide. Culture-sensitive comparison would
    /// therefore behave one way in Wintty.exe and another under
    /// `dotnet test`, where ICU collapses ignorable characters.
    /// </summary>
    public static readonly FrozenSet<string> Actions =
        FrozenSet.ToFrozenSet(Sorted, StringComparer.Ordinal);

    /// <summary>
    /// Windows argument separators. Exactly space and tab - not
    /// <see cref="char.IsWhiteSpace"/>, which also returns true for CR,
    /// LF, VT, FF, U+00A0 and U+3000. Windows tokenization treats those
    /// as ordinary argument characters, so counting one as a separator
    /// would splice into the middle of what the OS considers a single
    /// argument and corrupt argv[0].
    /// </summary>
    private static bool IsSeparator(char c) => c is ' ' or '\t';

    /// <summary>
    /// Advance past argv[0], leaving <paramref name="index"/> on the first
    /// code unit after the separator that ended it. Returns false when the
    /// command line carries no arguments at all.
    /// </summary>
    /// <remarks>
    /// argv[0] plays by its own rules: quotes toggle and are dropped,
    /// backslash escaping does not apply, and there is no leading separator
    /// skip. A command line starting with a separator therefore has an empty
    /// argv[0] and the exe path becomes the first argument, which is what
    /// both zig and the .NET host do.
    /// </remarks>
    private static bool TrySkipProgram(string commandLine, out int index)
    {
        index = 0;

        // An empty command line, or one starting with NUL, yields zero
        // arguments: the iterator completes before argv[0].
        if (string.IsNullOrEmpty(commandLine) || commandLine[0] == '\0') return false;

        var insideQuotes = false;
        while (index < commandLine.Length)
        {
            var c = commandLine[index];
            if (c == '\0') return false;
            index++;
            if (c == '"') insideQuotes = !insideQuotes;
            else if (IsSeparator(c) && !insideQuotes) break;
        }

        // Ran to the end of the string without an unquoted separator, or the
        // separator was the last character: no arguments follow.
        return index < commandLine.Length;
    }

    /// <summary>
    /// Advance <paramref name="index"/> to the next argument after argv[0]
    /// and report the span it occupies. Returns false at the end of the
    /// arguments.
    /// </summary>
    /// <remarks>
    /// Reproduces the argument boundaries of
    /// <c>std.process.Args.Iterator.Windows</c> (zig 0.16.0,
    /// <c>lib/std/process/Args.zig</c>), quote toggling and backslash
    /// escaping included, because that is the tokenizer libghostty runs on
    /// the string handed to <c>ghostty_init_wide</c>. A boundary this gets
    /// wrong is an edit landing in the middle of what the real parser reads
    /// as a single argument.
    ///
    /// Boundaries only, not decoding: the span is raw text, and raw and
    /// decoded differ wherever a quote or a backslash appears. A caller
    /// comparing the span against a literal has to decline any span carrying
    /// either character.
    ///
    /// Index-based over <c>char</c> for the same reason
    /// <see cref="TryRewrite"/> is: the command line is WTF-16 and may hold
    /// unpaired surrogates, which <c>Rune.DecodeFromUtf16</c> rejects. Every
    /// character switched on here is ASCII, so no surrogate half can be
    /// mistaken for one.
    /// </remarks>
    private static bool TryNextArg(
        string commandLine, ref int index, out int start, out int length)
    {
        start = 0;
        length = 0;

        while (index < commandLine.Length && IsSeparator(commandLine[index])) index++;
        if (index >= commandLine.Length || commandLine[index] == '\0') return false;

        start = index;
        var backslashes = 0;
        var insideQuotes = false;
        while (index < commandLine.Length)
        {
            var c = commandLine[index];
            if (c == '\0') break;

            if (IsSeparator(c))
            {
                backslashes = 0;
                if (!insideQuotes) break;
            }
            else if (c == '"')
            {
                // 2n backslashes leave the quote acting as a quote;
                // 2n + 1 escape it into a literal one.
                var escaped = backslashes % 2 != 0;
                backslashes = 0;
                if (!escaped)
                {
                    // A doubled quote inside quotes is one literal quote and
                    // does not toggle. Consuming the second one here is what
                    // keeps the toggle state right for everything after it.
                    if (insideQuotes &&
                        index + 1 < commandLine.Length &&
                        commandLine[index + 1] == '"')
                    {
                        index++;
                    }
                    else
                    {
                        insideQuotes = !insideQuotes;
                    }
                }
            }
            else if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                backslashes = 0;
            }

            index++;
        }

        length = index - start;
        return true;
    }

    /// <summary>
    /// Wintty's spelling of the flag that discards the default config
    /// files, and the libghostty key it is spliced into.
    /// </summary>
    /// <remarks>
    /// Rewritten rather than acted on here because the discard is not a
    /// thing the shell can do correctly. libghostty resets
    /// <c>config-default-files</c> at the top of <c>loadCliArgs</c>,
    /// records a replay marker, and rebuilds the config from that marker
    /// when the flag turns up - so the discard drops precisely what the
    /// default files contributed and nothing else. A shell-side
    /// reimplementation would have to reproduce that, and would still
    /// leave `no-config` on the command line for libghostty to report as
    /// an unknown key.
    /// </remarks>
    private const string NoConfigFlag = "--no-config";

    private const string NoConfigKey = "--config-default-files=false";

    /// <summary>
    /// <c>--config-file</c>, in the <c>--key=value</c> form libghostty
    /// documents, and bare for the case where the value is a separate
    /// argument.
    /// </summary>
    private const string ConfigFileFlag = "--config-file";

    /// <summary>
    /// Translate the Wintty config flags on <paramref name="commandLine"/>
    /// into what libghostty parses, and report what was asked for.
    /// </summary>
    /// <remarks>
    /// Matching is done on the raw span, so a flag written with quotes or
    /// backslashes inside the flag name itself is not recognised. That form
    /// degrades to a libghostty unknown-key diagnostic rather than to
    /// silence, and no realistic invocation writes it - a path argument
    /// carrying backslashes sits after the <c>=</c>, past the part compared
    /// here.
    /// </remarks>
    public static ConfigOverrides RewriteConfigFlags(string commandLine)
    {
        if (!TrySkipProgram(commandLine, out var i))
            return new ConfigOverrides(commandLine, false, false);

        var noConfig = false;
        var configFile = false;
        List<(int Start, int Length)>? splices = null;

        while (TryNextArg(commandLine, ref i, out var start, out var length))
        {
            var span = commandLine.AsSpan(start, length);

            // -e hands the rest of the line to the child command, so a flag
            // after it is the child's. Same rule IsHelpRequest applies, and
            // for the same reason: `wintty -e mytool --no-config` configures
            // mytool, not Wintty.
            if (span.SequenceEqual("-e")) break;

            if (span.SequenceEqual(NoConfigFlag))
            {
                noConfig = true;
                (splices ??= new List<(int, int)>()).Add((start, length));
                continue;
            }

            // Bare and `=`-joined both count. Only the flag name is compared,
            // so a value carrying backslashes or quotes still matches.
            if (span.StartsWith(ConfigFileFlag) &&
                (span.Length == ConfigFileFlag.Length || span[ConfigFileFlag.Length] == '='))
            {
                configFile = true;
            }
        }

        if (splices is null)
            return new ConfigOverrides(commandLine, noConfig, configFile);

        var sb = new StringBuilder(commandLine.Length + splices.Count * NoConfigKey.Length);
        var copied = 0;
        foreach (var (start, length) in splices)
        {
            sb.Append(commandLine, copied, start - copied);
            sb.Append(NoConfigKey);
            copied = start + length;
        }
        sb.Append(commandLine, copied, commandLine.Length - copied);

        return new ConfigOverrides(sb.ToString(), noConfig, configFile);
    }

    /// <summary>
    /// Rewrite <paramref name="commandLine"/> so libghostty sees a bare
    /// leading subcommand as its <c>+action</c> form, e.g.
    /// <c>wintty.exe list-themes</c> to <c>wintty.exe +list-themes</c>.
    /// Returns false and leaves the command line untouched when the first
    /// argument is not a bare alias.
    /// </summary>
    /// <remarks>
    /// The tokenizer mirrors <c>std.process.Args.Iterator.Windows</c>
    /// (zig 0.16.0, <c>lib/std/process/Args.zig</c>), because that is what
    /// libghostty runs on the string handed back. Divergence here would
    /// mean splicing at an offset the real parser reads differently.
    ///
    /// Scanning is index-based over <c>char</c> on purpose. The command
    /// line is WTF-16 and may hold unpaired surrogates, which
    /// <c>Rune.DecodeFromUtf16</c> rejects. Index-based scanning is also
    /// what makes the insertion point provably safe: the code unit before
    /// it is always a separator, so the <c>+</c> can never land between a
    /// high and a low surrogate.
    /// </remarks>
    public static bool TryRewrite(string commandLine, out string rewritten, out string? action)
    {
        rewritten = commandLine;
        action = null;

        // An empty command line, or one starting with NUL, yields zero
        // arguments: the iterator completes before argv[0].
        if (string.IsNullOrEmpty(commandLine) || commandLine[0] == '\0') return false;

        // argv[0] plays by its own rules: quotes toggle and are dropped,
        // backslash escaping does not apply, and there is no leading
        // separator skip. A command line starting with a separator
        // therefore has an empty argv[0] and the exe path becomes the
        // first argument, which is what both zig and the .NET host do.
        var i = 0;
        var insideQuotes = false;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\0') return false;
            i++;
            if (c == '"') insideQuotes = !insideQuotes;
            else if (IsSeparator(c) && !insideQuotes) break;
        }

        // Ran to the end of the string without an unquoted separator, or
        // the separator was the last character: no arguments follow.
        if (i >= commandLine.Length) return false;

        while (i < commandLine.Length && IsSeparator(commandLine[i])) i++;
        if (i >= commandLine.Length || commandLine[i] == '\0') return false;

        var start = i;
        while (i < commandLine.Length &&
               commandLine[i] != '\0' &&
               !IsSeparator(commandLine[i]))
        {
            i++;
        }

        var span = commandLine.AsSpan(start, i - start);

        // Decline anything carrying a quote or a backslash. Full
        // escaping (2n vs 2n+1 backslashes before a quote, doubled quotes
        // inside quotes) is real, but no action name needs any of it, so
        // declining keeps `wintty "list-themes"` behaving exactly as it
        // does today instead of requiring those rules to be reimplemented
        // correctly. With neither character present, this span is
        // byte-for-byte what zig's tokenizer produces.
        if (span.IndexOfAny('"', '\\') >= 0) return false;

        var candidate = span.ToString();
        if (!Actions.Contains(candidate)) return false;

        action = candidate;
        rewritten = string.Concat(commandLine.AsSpan(0, start), "+", commandLine.AsSpan(start));
        return true;
    }

    /// <summary>
    /// True when <paramref name="arg"/> looks like a bare subcommand the
    /// user got wrong, rather than a flag, a path, or an <c>-e</c>
    /// payload. Used to turn an unknown verb into an error instead of a
    /// silently ignored argument.
    /// </summary>
    public static bool LooksLikeCommand(string arg)
    {
        if (arg.Length == 0 || arg[0] is < 'a' or > 'z') return false;
        foreach (var c in arg)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Whether to print <see cref="RenderHelp"/> instead of running an
    /// action or starting the GUI. <paramref name="isAlias"/> is the
    /// result of <see cref="TryRewrite"/> over the same invocation.
    /// </summary>
    public static bool IsHelpRequest(string[] args, bool isAlias)
    {
        if (args.Length == 0) return false;

        // An explicit help command in the first position, either spelling.
        if (args[0] == "help" || args[0] == "+help") return true;

        // Any other action owns --help: it prints that action's own help,
        // which only libghostty can render.
        if (args[0].StartsWith('+') || isAlias) return false;

        // -e hands the rest of the line to the child command, so a help
        // flag anywhere on it is the child's, not ours. Checked before the
        // help flags rather than alongside them: detectSpecialCase in
        // src/cli/action.zig returns abort_if_no_action on -e regardless of
        // whether a help fallback was already recorded, so `--help -e foo`
        // has to run foo too, not just `-e foo --help`.
        foreach (var arg in args)
            if (arg == "-e") return false;

        foreach (var arg in args)
            if (arg == "--help" || arg == "-h" || arg == "/?") return true;

        return false;
    }

    /// <summary>
    /// Windows-native usage text. Replaces libghostty's <c>+help</c>
    /// output, which names `ghostty`, points the reader at
    /// <c>src/config/Config.zig</c>, and explains
    /// <c>open -na Ghostty.app</c> - all wrong on Windows.
    ///
    /// One command per line, rendered from <see cref="Sorted"/>, so a
    /// new upstream action appears here the moment the parity test forces
    /// it into the set. Upstream prints names without descriptions too,
    /// so there is no description text to drift.
    /// </summary>
    public static string RenderHelp(string programName)
    {
        var sb = new StringBuilder();
        sb.Append($"Usage: {programName} [command] [options]\n\n");
        sb.Append($"Run the {AppIdentity.ProductName} terminal emulator, or a helper command.\n\n");
        sb.Append(
            "If no command is given, run the terminal emulator. All configuration\n" +
            "keys are available as command line options in `--<key>=<value>` form,\n" +
            "using the same syntax as the config file, for example `--font-size=12`\n" +
            "or `--font-family=\"Fira Code\"`.\n\n");
        sb.Append($"`{programName} -e <command>` runs a command inside the terminal, for\n");
        sb.Append($"example `{programName} -e pwsh`.\n\n");

        sb.Append("Configuration options:\n\n");
        sb.Append(
            "  --config-file=<path>  Also load <path>, after the config file that\n" +
            "                        would be loaded anyway. Repeatable.\n" +
            "  --no-config           Ignore the config file entirely and run on\n" +
            "                        built-in defaults.\n\n");
        sb.Append(
            "The two are not mirror images. `--no-config` suppresses every source,\n" +
            $"including the {AppIdentity.ProductName}-only keys read outside the shared\n" +
            "config parser. `--config-file` supplies only the keys that parser\n" +
            $"handles, so {AppIdentity.ProductName}-only keys such as `vertical-tabs`\n" +
            "still come from the usual config file.\n\n");
        sb.Append(
            "Either option starts a separate instance, because a config cannot be\n" +
            "handed to a window that is already open.\n\n");

        sb.Append("Commands:\n\n");

        foreach (var name in Sorted)
            sb.Append($"  {name}\n");

        sb.Append("\nEach command also accepts the `+command` spelling inherited from\n");
        sb.Append($"Ghostty, for example `{programName} +list-themes`. Both forms work.\n\n");
        sb.Append($"Run `{programName} +<command> --help` for help on a single command.\n");

        return sb.ToString();
    }
}
