namespace Ghostty.Core.Clipboard;

/// <summary>
/// Mirrors ghostty_clipboard_e. Selection is a no-op on Windows because
/// Win32 has no PRIMARY-style selection clipboard; we keep the enum value
/// so the bridge can route requests defensively.
/// </summary>
public enum ClipboardKind
{
    Standard = 0,
    Selection = 1,
}
