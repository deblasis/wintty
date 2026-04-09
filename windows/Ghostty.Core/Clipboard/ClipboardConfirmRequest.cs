namespace Ghostty.Core.Clipboard;

/// <summary>
/// Mirrors ghostty_clipboard_request_e. The reason libghostty is asking
/// the user to confirm a clipboard operation.
/// </summary>
public enum ClipboardConfirmRequest
{
    Paste = 0,
    Osc52Read = 1,
    Osc52Write = 2,
}
