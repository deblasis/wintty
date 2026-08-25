using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Abstracts the Windows clipboard. The production implementation lives
/// in the WinUI project as WinUiClipboardBackend.
/// </summary>
public interface IClipboardBackend
{
    /// <summary>
    /// Returns the current clipboard text, or null when there is no
    /// text-format content. Returning null lets the caller propagate
    /// "nothing there" to libghostty's read_clipboard_cb so paste keybinds
    /// fall through to the terminal. Matches the macOS contract from
    /// NSPasteboard.getOpinionatedStringContents.
    /// </summary>
    ValueTask<string?> ReadTextAsync();

    /// <summary>
    /// The MIME names the clipboard is currently offering, in the order we
    /// prefer to serve them.
    ///
    /// Separate from <see cref="ReadAsync"/> because the Kitty clipboard
    /// protocol has an enumerate-only request that upstream deliberately
    /// exempts from the permission prompt: listing which formats exist
    /// leaks far less than handing over their contents, and prompting for
    /// it would make the protocol unusable. Answering a listing must
    /// therefore not read anything.
    /// </summary>
    ValueTask<IReadOnlyList<string>> GetAvailableMimesAsync();

    /// <summary>
    /// Read every representation whose MIME appears in
    /// <paramref name="accepted"/>. An empty accepted list means "anything
    /// you have". Representations that fail to read are omitted rather
    /// than surfaced as empty payloads.
    /// </summary>
    ValueTask<IReadOnlyList<ClipboardPayload>> ReadAsync(IReadOnlyList<string> accepted);

    /// <summary>
    /// Atomically writes one or more MIME-tagged payloads as a single
    /// Clipboard.SetContent call. Backend skips MIMEs it does not
    /// recognise. The caller has already filtered out unsupported MIMEs
    /// in the service layer; this is defence in depth.
    /// </summary>
    ValueTask WriteAsync(IReadOnlyList<ClipboardPayload> payloads);
}
