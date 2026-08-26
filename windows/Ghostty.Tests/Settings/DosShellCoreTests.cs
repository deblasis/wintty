using System;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The fake MS-DOS shell that backs the shader picker's preview and the
/// website demo (wintty.io/shaders, src/lib/dos-shell.ts). Output is
/// asserted byte-for-byte against the website's text: same banner, same
/// file table, same replies, so the in-app demo and the web demo read as
/// the same machine. Dates and times come from an injected clock so
/// nothing here depends on the day the test runs.
///
/// The shell is pure state plus string returns: no UI, no sink, so every
/// keystroke's VT bytes are inspectable directly, which is what makes
/// "Insert flips produce the right DECSCUSR bytes" a plain equality.
/// </summary>
public class DosShellCoreTests
{
    // A Tuesday afternoon, pinned so DIR dates, TIME, and DATE are all
    // deterministic.
    private static readonly DateTime FixedNow = new(2026, 8, 25, 15, 4, 5);

    private const string Prompt = "\x1b[34;1mC:\\>\x1b[37m ";

    private static DosShellCore NewCore() => new(() => FixedNow);

    /// <summary>
    /// Type a command and press Enter, returning only the Enter's bytes
    /// (newline, reply, next prompt). The character echo is discarded
    /// here: it is TypedCharactersEchoVerbatim's job to pin it.
    /// </summary>
    private static string Run(DosShellCore core, string command)
    {
        foreach (var ch in command) core.SendChar(ch);
        return core.SendKey(DosShellKey.Enter);
    }

    // Boot and prompt ---------------------------------------------------

    [Fact]
    public void BootIsGrayForegroundThenTheWebsiteBanner()
    {
        // The website's constructor writes FG_GRAY then the banner; the
        // prompt is a separate write, so Boot must not include it.
        Assert.Equal(
            "\x1b[37m" +
            "\r\n" +
            "Starting MS-DOS...\r\n" +
            "\r\n" +
            "\x1b[1;37mMicrosoft(R) MS-DOS(R) Version 6.22\r\n\x1b[37m" +
            "(C)Copyright Microsoft Corp 1981-1994.\r\n" +
            "\r\n" +
            "WINTTY Shader Lab Extension v1.0 installed.\r\n" +
            "\r\n" +
            "Type HELP for the command list.\r\n" +
            "\r\n",
            NewCore().Boot());
    }

    [Fact]
    public void PromptIsBluePathGrayTrailingSpace()
    {
        // The trailing space is load-bearing: the cursor (and the cursor
        // shaders) sit on it, and it is where typed characters land.
        Assert.Equal(Prompt, NewCore().NewPrompt());
    }

    // Line editing --------------------------------------------------------

    [Fact]
    public void TypedCharactersEchoVerbatim()
    {
        var core = NewCore();
        Assert.Equal("d", core.SendChar('d'));
        Assert.Equal("i", core.SendChar('i'));
        Assert.Equal("r", core.SendChar('r'));
    }

    [Fact]
    public void BackspaceErasesOneCharacterPerPress()
    {
        var core = NewCore();
        core.SendChar('a');
        core.SendChar('b');
        Assert.Equal("\b \b", core.SendKey(DosShellKey.Backspace));
        Assert.Equal("\b \b", core.SendKey(DosShellKey.Backspace));
        // At the prompt edge, Backspace is a silent no-op.
        Assert.Equal("", core.SendKey(DosShellKey.Backspace));
    }

    [Fact]
    public void EnterOnAnEmptyLineWritesOnlyTheNextPrompt()
    {
        // An empty command executes nothing: newline, prompt, no reply.
        Assert.Equal("\r\n" + Prompt, NewCore().SendKey(DosShellKey.Enter));
    }

    [Fact]
    public void EnterEchoesANewlineThenTheReplyThenTheNextPrompt()
    {
        var output = Run(NewCore(), "ver");
        Assert.StartsWith("\r\n", output, StringComparison.Ordinal);
        Assert.EndsWith(Prompt, output, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeClearsTheInputLineInPlace()
    {
        var core = NewCore();
        core.SendChar('a');
        core.SendChar('b');
        core.SendChar('c');
        // One backspace-erase per character already on the line.
        Assert.Equal("\b \b\b \b\b \b", core.SendKey(DosShellKey.Escape));
        // The line is really empty: Enter runs nothing.
        Assert.Equal("\r\n" + Prompt, core.SendKey(DosShellKey.Enter));
    }

    [Fact]
    public void CtrlCInterruptsTheLineWithoutRunningIt()
    {
        var core = NewCore();
        core.SendChar('d');
        core.SendChar('i');
        Assert.Equal("^C\r\n" + Prompt, core.SendKey(DosShellKey.CtrlC));
        // Nothing was executed, so there is nothing to recall.
        Assert.Equal("", core.SendKey(DosShellKey.Up));
    }

    // History recall ------------------------------------------------------

    [Fact]
    public void ArrowUpRecallsCommandsNewestFirstAndDownWalksBack()
    {
        var core = NewCore();
        Run(core, "ver");
        Run(core, "mem");

        // Recalling into an empty line just prints the entry.
        Assert.Equal("mem", core.SendKey(DosShellKey.Up));
        // Recalling over a printed entry erases it first.
        Assert.Equal("\b \b\b \b\b \bver", core.SendKey(DosShellKey.Up));
        Assert.Equal("\b \b\b \b\b \bmem", core.SendKey(DosShellKey.Down));
        // Down past the newest entry empties the line.
        Assert.Equal("\b \b\b \b\b \b", core.SendKey(DosShellKey.Down));
    }

    [Fact]
    public void ArrowUpStopsAtTheOldestEntry()
    {
        var core = NewCore();
        Run(core, "ver");
        Run(core, "mem");
        core.SendKey(DosShellKey.Up);
        core.SendKey(DosShellKey.Up);
        // Already at the oldest: pressing Up again changes nothing.
        Assert.Equal("", core.SendKey(DosShellKey.Up));
    }

    [Fact]
    public void EmptyCommandsDoNotEnterHistory()
    {
        var core = NewCore();
        core.SendKey(DosShellKey.Enter);
        core.SendKey(DosShellKey.Enter);
        Assert.Equal("", core.SendKey(DosShellKey.Up));
    }

    [Fact]
    public void RecallKeysAreSilentWithNoHistory()
    {
        var core = NewCore();
        Assert.Equal("", core.SendKey(DosShellKey.Up));
        Assert.Equal("", core.SendKey(DosShellKey.Down));
    }

    // Insert and cursor shape ---------------------------------------------

    [Fact]
    public void InsertFlipsCursorShapeAlternatelyAndStatePersists()
    {
        var core = NewCore();
        // The demo presses Insert at scattered points in the script, so
        // the flip state has to survive command boundaries.
        Assert.Equal("\x1b[5 q", core.SendKey(DosShellKey.Insert));
        Assert.Equal("\x1b[2 q", core.SendKey(DosShellKey.Insert));
        Run(core, "ver");
        Assert.Equal("\x1b[5 q", core.SendKey(DosShellKey.Insert));
    }

    // Command table ---------------------------------------------------------

    [Fact]
    public void DirListsTheWebsiteFileTable()
    {
        // Byte-for-byte the website's listing (same names, sizes, column
        // layout) with the clock's date in the stamp column.
        var output = Run(NewCore(), "dir");
        Assert.Equal(
            "\r\n Volume in drive C is WINTTY\r\n" +
            "\r\n" +
            " IO        SYS     40,766     8/25/2026\r\n" +
            " MSDOS     SYS     38,138     8/25/2026\r\n" +
            " COMMAND   COM     54,619     8/25/2026\r\n" +
            " AUTOEXEC  BAT        214     8/25/2026\r\n" +
            " CONFIG    SYS        168     8/25/2026\r\n" +
            " WINTTY             <DIR>     8/25/2026\r\n" +
            " CRT       GLS      1,842     8/25/2026\r\n" +
            " SCANLINE  GLS        916     8/25/2026\r\n" +
            " SNOWFALL  GLS      1,024     8/25/2026\r\n" +
            " AURORA    GLS      2,048     8/25/2026\r\n" +
            " PIPBOY    GLS      1,536     8/25/2026\r\n" +
            "         10 file(s)      141,271 bytes\r\n" +
            "          2 dir(s)  33,554,432 bytes free\r\n" +
            "\r\n",
            output);
    }

    [Fact]
    public void VerRepliesWithTheDOSVersion()
    {
        Assert.Equal(
            "\r\nMS-DOS Version 6.22\r\nwintty shader gallery, live preview\r\n\r\n",
            Run(NewCore(), "ver"));
    }

    [Fact]
    public void TimeRepliesFromTheClock()
    {
        Assert.Equal(
            "\r\nCurrent time is 3:04:05 PM\r\n\r\n",
            Run(NewCore(), "time"));
    }

    [Fact]
    public void DateRepliesFromTheClock()
    {
        Assert.Equal(
            "\r\nCurrent date is Tue Aug 25 2026\r\n\r\n",
            Run(NewCore(), "date"));
    }

    [Fact]
    public void EchoPrintsItsArgumentWithCaseAndInnerSpacingIntact()
    {
        Assert.Equal(
            "\r\nHello   World\r\n\r\n",
            Run(NewCore(), "echo Hello   World"));
    }

    [Fact]
    public void EchoWithoutAnArgumentSaysEchoIsOn()
    {
        Assert.Equal("\r\nECHO is on\r\n\r\n", Run(NewCore(), "echo"));
    }

    [Fact]
    public void ClsClearsTheScreenAndHomesTheCursor()
    {
        Assert.Equal("\x1b[2J\x1b[H", Run(NewCore(), "cls"));
    }

    [Fact]
    public void HelpPrintsTheWebsiteCommandListIncludingTheRecallHint()
    {
        Assert.Equal(
            "\r\n" +
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
            "Up/Down arrows recall previous commands.\r\n",
            Run(NewCore(), "help"));
    }

    [Fact]
    public void QuestionMarkIsHelp()
    {
        Assert.Contains("Available commands:", Run(NewCore(), "?"));
    }

    [Fact]
    public void TypeShowsAutoexecBat()
    {
        Assert.Equal(
            "\r\n" +
            "@ECHO OFF\r\n" +
            "PROMPT $p$g\r\n" +
            "SET SHADER=CRT.GLS\r\n" +
            "LH C:\\WINTTY\\SHADERLAB.EXE /GALLERY\r\n" +
            "\r\n",
            Run(NewCore(), "type autoexec.bat"));
    }

    [Fact]
    public void TypeAcceptsTheShortNameAndAnyCase()
    {
        Assert.StartsWith("\r\nDEVICE=C:", Run(NewCore(), "Type CONFIG"), StringComparison.Ordinal);
    }

    [Fact]
    public void TypeWithoutAParameterReportsItMissing()
    {
        Assert.Equal(
            "\r\nRequired parameter missing\r\n\r\n",
            Run(NewCore(), "type"));
    }

    [Fact]
    public void TypeOfAnUnknownFileSaysSo()
    {
        Assert.Equal(
            "\r\nFile not found - nope.txt\r\n\r\n",
            Run(NewCore(), "type nope.txt"));
    }

    [Fact]
    public void ModeCursorSetsEachShape()
    {
        Assert.Equal("\x1b[4 q", Run(NewCore(), "mode cursor=underline"));
        Assert.Equal("\x1b[2 q", Run(NewCore(), "MODE CURSOR=BLOCK"));
        Assert.Equal("\x1b[5 q", Run(NewCore(), "mode cursor=bar"));
    }

    [Fact]
    public void ModeCursorHelpWritesNoCursorSequence()
    {
        // The website's table has no sequence for HELP and writes nothing;
        // a literal "undefined" here would type garbage into the preview.
        Assert.Equal("", Run(NewCore(), "mode cursor=help"));
    }

    [Fact]
    public void ModeWithBadParametersPrintsUsage()
    {
        Assert.Equal(
            "\r\nInvalid parameters - cursor\r\n" +
            "\r\nUsage: MODE CURSOR=BAR|BLOCK|UNDERLINE\r\n\r\n",
            Run(NewCore(), "mode cursor"));
    }

    [Fact]
    public void ModeWithNoParametersPrintsUsage()
    {
        Assert.Equal(
            "\r\nInvalid parameters - \r\n" +
            "\r\nUsage: MODE CURSOR=BAR|BLOCK|UNDERLINE\r\n\r\n",
            Run(NewCore(), "mode"));
    }

    [Fact]
    public void MemReportsConventionalMemory()
    {
        Assert.Equal(
            "\r\n  655,360 bytes total conventional memory\r\n" +
            "  655,360 bytes available to MS-DOS\r\n" +
            "  633,168 largest executable program size\r\n\r\n",
            Run(NewCore(), "mem"));
    }

    [Fact]
    public void UnknownCommandIsBadCommandOrFileName()
    {
        Assert.Equal(
            "\r\nBad command or file name\r\n\r\n",
            Run(NewCore(), "del *.*"));
    }

    [Fact]
    public void CommandsMatchCaseInsensitively()
    {
        Assert.StartsWith("\r\n Volume in drive C", Run(NewCore(), "DiR"), StringComparison.Ordinal);
    }

    [Fact]
    public void EnterPushesTheTrimmedCommandToHistory()
    {
        var core = NewCore();
        // Whitespace around the command is trimmed before dispatch and
        // before history, so recall prints the clean word.
        core.SendChar(' ');
        core.SendChar('v');
        core.SendChar('e');
        core.SendChar('r');
        core.SendChar(' ');
        core.SendKey(DosShellKey.Enter);
        Assert.Equal("ver", core.SendKey(DosShellKey.Up));
    }
}
