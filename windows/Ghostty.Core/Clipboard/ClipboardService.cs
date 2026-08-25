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

    /// <summary>
    /// The Kitty clipboard read path. Returns the representations to hand
    /// back plus the names of everything the clipboard is offering.
    ///
    /// The two are not the same list and must not be collapsed into one:
    /// `available` is what exists, `contents` is what the caller asked for
    /// and we could produce. A caller that filtered to image/png still
    /// wants to be told text/plain is there.
    /// </summary>
    public async ValueTask<ClipboardReadOutcome> HandleKittyReadAsync(
        ClipboardKind kind,
        IReadOnlyList<string> accepted,
        bool listing)
    {
        if (kind is ClipboardKind.Selection or ClipboardKind.Primary)
            return ClipboardReadOutcome.Unsupported;

        try
        {
            var available = await _backend.GetAvailableMimesAsync();

            // A listing enumerates and stops. Reading here would defeat the
            // point of upstream exempting listings from the prompt.
            if (listing)
            {
                return available.Count == 0
                    ? ClipboardReadOutcome.Empty
                    : new ClipboardReadOutcome(
                        ClipboardReadStatus.Ok, Array.Empty<ClipboardPayload>(), available);
            }

            var contents = await _backend.ReadAsync(accepted);
            if (contents.Count == 0 && available.Count == 0)
                return ClipboardReadOutcome.Empty;

            return new ClipboardReadOutcome(ClipboardReadStatus.Ok, contents, available);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Same rule as the paste path: any IClipboardBackend can throw,
            // and a clipboard held open by another process is routine. Empty
            // rather than Unsupported -- the runtime does support this, we
            // just could not read right now.
            return ClipboardReadOutcome.Empty;
        }
    }

    public async ValueTask HandleWriteAsync(
        ClipboardKind kind,
        IReadOnlyList<ClipboardPayload> payloads,
        bool confirm,
        IntPtr originSurface = default,
        ClipboardConfirmRequest request = ClipboardConfirmRequest.Osc52Write)
    {
        // Neither Selection nor Primary exists on Windows. Core rejects a
        // Kitty write to one before any prompt (it asks supportsClipboard
        // first and answers ENOSYS), so reaching here with one would mean
        // the runtime advertised a clipboard it does not have.
        if (kind is ClipboardKind.Selection or ClipboardKind.Primary)
            return;
        if (payloads.Count == 0)
            return;

        var supported = payloads
            .Where(p => WindowsClipboardFormatMap.FromMimeForWrite(p.Mime) is not null)
            .ToList();

        if (supported.Count == 0)
            return;

        // Mirrors the macOS apprt assertion. libghostty's contract is at
        // most one text/plain entry per write; the WinUI DataPackage
        // assumes this.
        Debug.Assert(
            supported.Count(p => p.Mime == ClipboardMime.TextPlain) <= 1,
            "clipboard payloads should have at most one text/plain entry");

        if (confirm)
        {
            // Everything being written goes into the prompt, not just the
            // text/plain entry. This used to drop the whole write when there
            // was no text/plain to preview, which silently discarded an
            // html-only write; the dialog now picks its own preview and says
            // so when a payload cannot be rendered.
            var snapshot = new ClipboardConfirmSnapshot(
                supported,
                supported.Select(p => p.Mime).ToList(),
                Name: null,
                CanRemember: false);

            var decision = await _confirmer.ConfirmAsync(
                snapshot,
                request,
                originSurface);
            if (!decision.Accepted)
                return;
        }

        await _backend.WriteAsync(supported);
    }

    public ValueTask<ClipboardConfirmResult> HandleConfirmAsync(
        ClipboardConfirmSnapshot snapshot,
        ClipboardConfirmRequest request,
        IntPtr originSurface = default)
    {
        // Pass-through to the platform confirmer. Kept as a service
        // method (rather than calling the confirmer directly from the
        // bridge) so the routing rules for what gets confirmed and how
        // live in one testable place if they grow more complex later.
        return _confirmer.ConfirmAsync(snapshot, request, originSurface);
    }
}
