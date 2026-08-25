using System;
using System.Threading.Tasks;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// What the user decided in the clipboard permission prompt.
/// </summary>
/// <param name="Accepted">True when the user allowed the operation.</param>
/// <param name="Remember">
/// True when the user asked not to be prompted again for this session.
/// Only ever set when the request said a grant may be offered; a
/// remembered denial is not a thing libghostty models, so this is
/// meaningless unless Accepted is true.
/// </param>
public readonly record struct ClipboardConfirmResult(bool Accepted, bool Remember)
{
    /// <summary>The safe default, and what every failure path returns.</summary>
    public static readonly ClipboardConfirmResult Denied = new(false, false);

    public static ClipboardConfirmResult Allow(bool remember = false) => new(true, remember);
}

/// <summary>
/// Renders the clipboard confirmation dialog libghostty asks for via
/// confirm_read_clipboard_cb. The production implementation is
/// DialogClipboardConfirmer in the WinUI project.
/// </summary>
public interface IClipboardConfirmer
{
    /// <summary>
    /// Show the prompt and return what the user chose. Implementations
    /// must default to Denied for safety and must be safe to call
    /// concurrently.
    ///
    /// Takes the whole request rather than a preformatted preview string:
    /// the prompt is the only thing standing between a remote program and
    /// the user's clipboard, so it has to be able to show what is actually
    /// being asked for -- an image as an image, the requesting party's
    /// name, the full list of representations -- rather than whatever one
    /// string the caller chose to render.
    /// </summary>
    /// <param name="originSurface">
    /// Opaque handle identifying the surface (and therefore the window)
    /// that triggered the request. WinUI uses this to resolve the correct
    /// XamlRoot so the dialog appears on the originating window instead
    /// of whichever window happens to be first in the surfaces registry.
    /// Pass <see cref="IntPtr.Zero"/> when no origin is available; the
    /// implementation may then fall back to any active root.
    /// </param>
    ValueTask<ClipboardConfirmResult> ConfirmAsync(
        ClipboardConfirmSnapshot request,
        ClipboardConfirmRequest reason,
        IntPtr originSurface);
}
