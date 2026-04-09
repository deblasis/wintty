using System;
using System.Threading.Tasks;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Renders the clipboard confirmation dialog libghostty asks for via
/// confirm_read_clipboard_cb (paste, OSC 52 read, OSC 52 write). The
/// production implementation is DialogClipboardConfirmer in the WinUI
/// project.
/// </summary>
public interface IClipboardConfirmer
{
    /// <summary>
    /// Show a dialog with the supplied preview as the body and return
    /// true if the user accepts. Implementations must default to Cancel
    /// (return false) for safety and must be safe to call concurrently.
    /// </summary>
    /// <param name="originSurface">
    /// Opaque handle identifying the surface (and therefore the window)
    /// that triggered the request. WinUI uses this to resolve the correct
    /// XamlRoot so the dialog appears on the originating window instead
    /// of whichever window happens to be first in the surfaces registry.
    /// Pass <see cref="IntPtr.Zero"/> when no origin is available; the
    /// implementation may then fall back to any active root.
    /// </param>
    ValueTask<bool> ConfirmAsync(string preview, ClipboardConfirmRequest request, IntPtr originSurface);
}
