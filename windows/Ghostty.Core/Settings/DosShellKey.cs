namespace Ghostty.Core.Settings;

/// <summary>
/// A key the fake DOS shell consumes (see <see cref="DosShellCore"/>).
/// Everything else is either printable text (delivered as characters,
/// like WM_CHAR) or not the fake shell's business. The WinUI side maps
/// virtual keys onto this through <see cref="PreviewKeyMap"/>.
/// </summary>
internal enum DosShellKey
{
    Enter,
    Backspace,
    Up,
    Down,
    Escape,
    Insert,

    /// <summary>Ctrl+C: the DOS interrupt, "^C" plus a fresh prompt.</summary>
    CtrlC,
}
