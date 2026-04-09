using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Abstracts the Windows clipboard so the service layer is unit-testable
/// without WinUI. The production implementation lives in the WinUI
/// project as WinUiClipboardBackend.
/// </summary>
public interface IClipboardBackend
{
    /// <summary>
    /// Returns the current clipboard text, or null when there is no
    /// text-format content. Returning null lets the caller propagate
    /// "false" to libghostty's read_clipboard_cb so paste keybinds
    /// fall through to the terminal. Matches the macOS contract from
    /// NSPasteboard.getOpinionatedStringContents.
    /// </summary>
    ValueTask<string?> ReadTextAsync();

    /// <summary>
    /// Atomically writes one or more MIME-tagged payloads as a single
    /// Clipboard.SetContent call. Backend skips MIMEs it does not
    /// recognise. The caller has already filtered out unsupported MIMEs
    /// in the service layer; this is defence in depth.
    /// </summary>
    ValueTask WriteAsync(IReadOnlyList<ClipboardPayload> payloads);
}
