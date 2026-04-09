namespace Ghostty.Core.Clipboard;

/// <summary>
/// One MIME-tagged clipboard entry. Mirrors ghostty_clipboard_content_s.
/// </summary>
public readonly record struct ClipboardPayload(string Mime, string Data);
