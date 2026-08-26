using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ghostty.Core.Settings;

/// <summary>
/// The fake MS-DOS shell behind the shader picker's preview, ported from
/// the website demo (wintty.io/shaders, src/lib/dos-shell.ts): same
/// banner, same command table, same replies, same line editing, same
/// Insert-driven cursor flips (DECSCUSR), which are the exact event the
/// mode-change cursor shaders animate on. Everything is canned and local;
/// there is no filesystem and no process to reach.
///
/// Pure state plus string returns: no UI, no sink, no threading. Both the
/// autoplay feed and the user's own keystrokes drive this one instance,
/// so scripted typing and human typing are indistinguishable to the
/// surface. SGR foregrounds only, never a background: the terminal theme
/// is the only background, so fullscreen shaders light up where text is
/// drawn instead of stopping at a palette-resolved bg cell (website
/// lesson).
/// </summary>
internal sealed class DosShellCore
{
    // SGR foregrounds only; see the class remarks.
    private const string FgGray = "\x1b[37m";
    private const string FgBright = "\x1b[1;37m";
    private const string FgBlue = "\x1b[34;1m";

    private const string PromptText = FgBlue + "C:\\>" + FgGray + " ";

    // DECSCUSR shapes, the payload of Insert and MODE CURSOR.
    private const string CursorBar = "\x1b[5 q";
    private const string CursorBlock = "\x1b[2 q";
    private const string CursorUnderline = "\x1b[4 q";

    private const string BannerText =
        "\r\n" +
        "Starting MS-DOS...\r\n" +
        "\r\n" +
        FgBright + "Microsoft(R) MS-DOS(R) Version 6.22\r\n" + FgGray +
        "(C)Copyright Microsoft Corp 1981-1994.\r\n" +
        "\r\n" +
        "WINTTY Shader Lab Extension v1.0 installed.\r\n" +
        "\r\n" +
        "Type HELP for the command list.\r\n" +
        "\r\n";

    private const string HelpText =
        "Available commands:\r\n" +
        "\r\n" +
        "  DIR        List the C: drive (DOS system files + shaders)\r\n" +
        "  TYPE file  Show a file (AUTOEXEC.BAT, CONFIG.SYS)\r\n" +
        "  VER        Show the DOS version\r\n" +
        "  TIME       Show the time\r\n" +
        "  ECHO text  Print text\r\n" +
        "  CLS        Clear the screen\r\n" +
        "  HELP       This list\r\n" +
        "\r\n" +
        "Up/Down arrows recall previous commands.\r\n";

    private const string AutoexecText =
        "@ECHO OFF\r\n" +
        "PROMPT $p$g\r\n" +
        "SET SHADER=CRT.GLS\r\n" +
        "LH C:\\WINTTY\\SHADERLAB.EXE /GALLERY\r\n";

    private const string ConfigText =
        "DEVICE=C:\\WINTTY\\VTWASM.SYS\r\n" +
        "DOS=HIGH,UMB\r\n" +
        "FILES=40\r\n" +
        "STACKS=9,256\r\n";

    /// <summary>
    /// The C: root of an old DOS box: system files where they belong, plus
    /// the shader lab's files. Name split into base and extension because
    /// the listing pads the two columns independently; a null size marks
    /// the one directory.
    /// </summary>
    private static readonly (string Base, string Ext, int? Size)[] DirFiles =
    [
        ("IO", "SYS", 40766),
        ("MSDOS", "SYS", 38138),
        ("COMMAND", "COM", 54619),
        ("AUTOEXEC", "BAT", 214),
        ("CONFIG", "SYS", 168),
        ("WINTTY", "", null),
        ("CRT", "GLS", 1842),
        ("SCANLINE", "GLS", 916),
        ("SNOWFALL", "GLS", 1024),
        ("AURORA", "GLS", 2048),
        ("PIPBOY", "GLS", 1536),
    ];

    private readonly Func<DateTime> _clock;

    private string _input = "";
    private bool _cursorBar;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    /// <param name="clock">
    /// Where DIR's date stamps and the TIME/DATE replies read the wall
    /// clock from. Injected in tests; production uses local time, like
    /// the website's Date().
    /// </param>
    public DosShellCore(Func<DateTime>? clock = null)
    {
        _clock = clock ?? DefaultNow;
    }

    private static DateTime DefaultNow() => DateTime.Now;

    /// <summary>
    /// The boot sequence: gray foreground, then the banner. One write, and
    /// deliberately without the prompt, which is its own write so the
    /// first one can land before the demo starts typing at the second.
    /// </summary>
    public string Boot() => FgGray + BannerText;

    /// <summary>The prompt segment, and a fresh empty input line.</summary>
    public string NewPrompt()
    {
        _input = "";
        return PromptText;
    }

    // High surrogate held across SendChar calls. WinUI delivers an
    // astral scalar as two character events; the pair must reach the
    // line and the echo as one character, or each half encodes as its
    // own U+FFFD and the visible line corrupts.
    private char _pendingHighSurrogate;

    /// <summary>
    /// A printable character: append to the line, echo verbatim. A
    /// surrogate pair is assembled first and echoes as the one character
    /// it is; a lone half is not text and never enters the line.
    /// </summary>
    public string SendChar(char ch)
    {
        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            return "";
        }
        if (char.IsLowSurrogate(ch))
        {
            if (_pendingHighSurrogate == '\0') return "";
            var pair = string.Concat(_pendingHighSurrogate.ToString(), ch.ToString());
            _pendingHighSurrogate = '\0';
            _input += pair;
            return pair;
        }
        _pendingHighSurrogate = '\0';
        _input += ch;
        return ch.ToString();
    }

    /// <summary>
    /// One non-printable key, returning its VT bytes. Keys that change
    /// nothing return the empty string, so a caller writing the result
    /// unconditionally stays correct.
    /// </summary>
    public string SendKey(DosShellKey key)
    {
        // A key press ends any half-delivered pair: a high surrogate
        // must be completed by the immediately following character, so
        // one held across an Enter or a Backspace is dropped, not
        // spliced into the next line.
        _pendingHighSurrogate = '\0';
        switch (key)
        {
            case DosShellKey.Enter:
            {
                var response = Execute(_input.Trim());
                _input = "";
                return "\r\n" + response + PromptText;
            }
            case DosShellKey.Backspace:
                if (_input.Length == 0) return "";
                _input = _input[..^1];
                return "\b \b";
            case DosShellKey.Up:
                if (_history.Count == 0) return "";
                _historyIndex = Math.Max(
                    0, _historyIndex < 0 ? _history.Count - 1 : _historyIndex - 1);
                return ReplaceInput(_history[_historyIndex]);
            case DosShellKey.Down:
                if (_historyIndex < 0) return "";
                _historyIndex++;
                if (_historyIndex >= _history.Count)
                {
                    _historyIndex = -1;
                    return ReplaceInput("");
                }
                return ReplaceInput(_history[_historyIndex]);
            case DosShellKey.Escape:
                return ReplaceInput("");
            case DosShellKey.Insert:
                // Insert flips insert/overwrite mode; the visible cursor
                // switches bar and block via DECSCUSR, which is exactly the
                // shape change the mode-change cursor shaders animate on.
                _cursorBar = !_cursorBar;
                return _cursorBar ? CursorBar : CursorBlock;
            case DosShellKey.CtrlC:
                // The DOS interrupt: kill the line, do not run it.
                _input = "";
                return "^C\r\n" + PromptText;
            default:
                return "";
        }
    }

    /// <summary>
    /// Erase the current input line and show <paramref name="next"/> in
    /// its place: one backspace-erase per character already on the line.
    /// </summary>
    private string ReplaceInput(string next)
    {
        var erase = _input.Length == 0
            ? ""
            : string.Concat(Enumerable.Repeat("\b \b", _input.Length));
        _input = next;
        return erase + next;
    }

    private string Execute(string raw)
    {
        if (raw.Length == 0) return "";
        _history.Add(raw);
        _historyIndex = -1;

        var space = raw.IndexOf(' ');
        var command = (space < 0 ? raw : raw[..space]).ToUpperInvariant();
        var arg = space < 0 ? "" : raw[(space + 1)..].Trim();

        switch (command)
        {
            case "DIR":
                return Dir();
            case "VER":
                return "\r\nMS-DOS Version 6.22\r\nwintty shader gallery, live preview\r\n\r\n";
            case "TIME":
                return $"\r\nCurrent time is {_clock().ToString("h:mm:ss tt", CultureInfo.InvariantCulture)}\r\n\r\n";
            case "DATE":
                // dd, not d: the website's toDateString zero-pads the day
                // ("Tue Sep 01 2026"), and a single-digit day must read the
                // same here.
                return $"\r\nCurrent date is {_clock().ToString("ddd MMM dd yyyy", CultureInfo.InvariantCulture)}\r\n\r\n";
            case "ECHO":
                return $"\r\n{(arg.Length > 0 ? arg : "ECHO is on")}\r\n\r\n";
            case "CLS":
                return "\x1b[2J\x1b[H";
            case "HELP":
            case "?":
                return "\r\n" + HelpText;
            case "TYPE":
                return TypeFile(arg);
            case "MODE":
                return Mode(arg);
            case "MEM":
                return "\r\n  655,360 bytes total conventional memory\r\n" +
                       "  655,360 bytes available to MS-DOS\r\n" +
                       "  633,168 largest executable program size\r\n\r\n";
            default:
                return "\r\nBad command or file name\r\n\r\n";
        }
    }

    private string Dir()
    {
        var builder = new StringBuilder();
        builder.Append("\r\n Volume in drive C is WINTTY\r\n\r\n");
        long bytes = 0;
        var files = 0;
        var dirs = 1;
        var stamp = _clock().ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        foreach (var (nameBase, ext, size) in DirFiles)
        {
            builder.Append(' ')
                .Append(nameBase.PadRight(9)).Append(' ')
                .Append(ext.PadRight(4)).Append(' ');
            if (size is { } fileBytes)
            {
                builder.Append(fileBytes.ToString("N0", CultureInfo.InvariantCulture).PadLeft(9));
                files++;
                bytes += fileBytes;
            }
            else
            {
                builder.Append("<DIR>");
                dirs++;
            }
            builder.Append("     ").Append(stamp).Append("\r\n");
        }
        builder.Append("        ").Append(files.ToString().PadLeft(3))
            .Append(" file(s) ")
            .Append(bytes.ToString("N0", CultureInfo.InvariantCulture).PadLeft(12))
            .Append(" bytes\r\n");
        builder.Append("        ").Append(dirs.ToString().PadLeft(3))
            .Append(" dir(s)  33,554,432 bytes free\r\n\r\n");
        return builder.ToString();
    }

    private static string TypeFile(string arg)
    {
        // Name matching ignores case and inner whitespace: TYPE  AUTOEXEC
        // .BAT reads the same file as type autoexec.bat.
        var name = new string(arg
            .ToUpperInvariant()
            .Where(ch => !char.IsWhiteSpace(ch))
            .ToArray());
        if (name is "AUTOEXEC.BAT" or "AUTOEXEC")
            return "\r\n" + AutoexecText + "\r\n";
        if (name is "CONFIG.SYS" or "CONFIG")
            return "\r\n" + ConfigText + "\r\n";
        if (name.Length == 0)
            return "\r\nRequired parameter missing\r\n\r\n";
        return $"\r\nFile not found - {arg}\r\n\r\n";
    }

    private static string Mode(string arg)
    {
        // MODE CURSOR=BAR|BLOCK|UNDERLINE flips the terminal cursor shape
        // via DECSCUSR, the same event Insert fires.
        var shape = CursorShapeFor(arg);
        return shape is null
            ? $"\r\nInvalid parameters - {arg}\r\n" +
              "\r\nUsage: MODE CURSOR=BAR|BLOCK|UNDERLINE\r\n\r\n"
            : shape;
    }

    /// <summary>
    /// The DECSCUSR bytes for a MODE CURSOR argument, or null when the
    /// argument is not that form at all. CURSOR=HELP matches the website's
    /// grammar but its table has no sequence, so it writes nothing rather
    /// than typing a placeholder into the preview.
    /// </summary>
    private static string? CursorShapeFor(string arg)
    {
        var equals = arg.IndexOf('=');
        if (equals < 0) return null;
        var name = arg[..equals].Trim();
        var value = arg[(equals + 1)..].Trim();
        if (!name.Equals("CURSOR", StringComparison.OrdinalIgnoreCase)) return null;
        return value.ToUpperInvariant() switch
        {
            "BAR" => CursorBar,
            "BLOCK" => CursorBlock,
            "UNDERLINE" => CursorUnderline,
            "HELP" => "",
            _ => null,
        };
    }
}
