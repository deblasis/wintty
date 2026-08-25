namespace Ghostty.Core.Clipboard;

/// <summary>
/// Mirrors ghostty_clipboard_e. Neither Selection nor Primary has a
/// Win32 equivalent: there is no PRIMARY-style selection buffer. We keep
/// the values so the bridge can route requests defensively and answer
/// Unsupported rather than silently succeeding.
/// </summary>
public enum ClipboardKind
{
    Standard = 0,
    Selection = 1,
    Primary = 2,
}
