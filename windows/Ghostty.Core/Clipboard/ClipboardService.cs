using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Pure-logic clipboard service. Mediates between libghostty's three
/// clipboard callbacks and the platform backend/confirmer. Knows nothing
/// about WinUI 3 and is fully unit-tested in Ghostty.Tests.
///
/// Key rules baked in:
///   * Selection clipboard is a no-op on Windows (no PRIMARY-style buffer).
///   * Backend exceptions on read are swallowed and surface as null so
///     paste keybinds fall through to the terminal.
///   * Writes never call the backend with an empty payload list, and
///     never call the backend if no payload has a known MIME (don't
///     clear the clipboard with an empty package).
/// </summary>
public sealed class ClipboardService
{
    private readonly IClipboardBackend _backend;
    private readonly IClipboardConfirmer _confirmer;

    public ClipboardService(IClipboardBackend backend, IClipboardConfirmer confirmer)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(confirmer);
        _backend = backend;
        _confirmer = confirmer;
    }

    public async ValueTask<string?> HandleReadAsync(ClipboardKind kind)
    {
        if (kind == ClipboardKind.Selection)
            return null;

        try
        {
            return await _backend.ReadTextAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Interface boundary: any IClipboardBackend can throw. Translate
            // to null so the paste keybind falls through to the terminal,
            // matching macOS. Fatal runtime conditions are still allowed to
            // propagate so we do not mask real crashes.
            return null;
        }
    }

    public async ValueTask HandleWriteAsync(
        ClipboardKind kind,
        IReadOnlyList<ClipboardPayload> payloads,
        bool confirm,
        IntPtr originSurface = default)
    {
        if (kind == ClipboardKind.Selection)
            return;
        if (payloads.Count == 0)
            return;

        var supported = payloads
            .Where(p => WindowsClipboardFormatMap.FromMime(p.Mime) is not null)
            .ToList();

        if (supported.Count == 0)
            return;

        // Mirrors the macOS apprt assertion. libghostty's contract is at
        // most one text/plain entry per write; the confirmation preview
        // and the WinUI DataPackage both assume this.
        Debug.Assert(
            supported.Count(p => p.Mime == ClipboardMime.TextPlain) <= 1,
            "clipboard payloads should have at most one text/plain entry");

        if (confirm)
        {
            // Need a text/plain entry to show as the dialog preview.
            // No preview means we drop the write rather than render an
            // empty or HTML-only dialog.
            if (supported.FirstOrDefault(p => p.Mime == ClipboardMime.TextPlain) is not { Mime: not null } textPlain)
                return;

            // libghostty's setClipboard with confirm=true is OSC 52 write
            // (the only path that asks for confirmation on writes).
            var accepted = await _confirmer.ConfirmAsync(
                textPlain.Data,
                ClipboardConfirmRequest.Osc52Write,
                originSurface);
            if (!accepted)
                return;
        }

        await _backend.WriteAsync(supported);
    }

    public ValueTask<bool> HandleConfirmAsync(string text, ClipboardConfirmRequest request, IntPtr originSurface = default)
    {
        // Pass-through to the platform confirmer. Kept as a service
        // method (rather than calling the confirmer directly from the
        // bridge) so the routing rules for what gets confirmed and how
        // live in one testable place if they grow more complex later.
        return _confirmer.ConfirmAsync(text, request, originSurface);
    }
}
