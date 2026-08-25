using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Ghostty.Core.Interop;
using Ghostty.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Ghostty.Hosting;

/// <summary>
/// Marshals libghostty clipboard callbacks into ClipboardService calls
/// and back. Owns the threading model: native callbacks return
/// immediately, all clipboard / dialog work runs inside
/// DispatcherQueue.TryEnqueue, and SurfaceCompleteClipboardRequest
/// is invoked once per read/confirm regardless of success or failure
/// (so libghostty never leaks request state).
///
/// Surface liveness is checked via the supplied IsSurfaceAlive callback
/// before completing requests, in case the TerminalControl was disposed
/// between the dispatch and the continuation.
///
/// Lifetime story: the IsSurfaceAlive check before
/// SurfaceCompleteClipboardRequest is intentional. When a surface is
/// destroyed by libghostty, libghostty also frees any pending clipboard
/// request state for that surface. If the surface dies mid-flight we
/// skip the completion call rather than calling it on a freed handle
/// (use-after-free). The same reasoning applies to the dispatcher
/// shutdown path: if TryEnqueue succeeds but the queue drops the
/// callback during shutdown, the surface itself is being destroyed
/// shortly after, and libghostty cleans up the request state via the
/// surface destroy path.
/// </summary>
internal sealed class ClipboardBridge
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ClipboardService _service;
    private readonly Func<IntPtr, IntPtr> _resolveSurface;   // userdata -> surface
    private readonly Func<IntPtr, bool> _isSurfaceAlive;     // surface  -> alive?
    private readonly ILogger<ClipboardBridge> _logger;

    public ClipboardBridge(
        DispatcherQueue dispatcher,
        ClipboardService service,
        Func<IntPtr, IntPtr> resolveSurface,
        Func<IntPtr, bool> isSurfaceAlive,
        ILogger<ClipboardBridge> logger)
    {
        _dispatcher = dispatcher;
        _service = service;
        _resolveSurface = resolveSurface;
        _isSurfaceAlive = isSurfaceAlive;
        _logger = logger;
    }

    // read_clipboard_cb

    public GhosttyClipboardReadResult HandleRead(
        IntPtr userdata,
        GhosttyClipboard kind,
        IntPtr state,
        IntPtr mimeFilter,
        UIntPtr mimeFilterLen,
        bool listing)
    {
        var surface = _resolveSurface(userdata);
        if (surface == IntPtr.Zero)
            return GhosttyClipboardReadResult.Unsupported;

        var managedKind = (ClipboardKind)kind;
        if (managedKind is ClipboardKind.Selection or ClipboardKind.Primary)
        {
            // Win32 has no PRIMARY-style buffer. Unsupported, not
            // Unavailable: libghostty gates the mode 5522 report on the
            // difference, and answering "empty" here would advertise a
            // selection clipboard we do not have.
            return GhosttyClipboardReadResult.Unsupported;
        }

        // The mime filter and the `listing` flag are not honoured yet: this
        // path still serves text/plain and nothing else, so there is nothing
        // to filter and nothing to enumerate. Reading them here and ignoring
        // them would read as support that does not exist. Serving the other
        // representations is what makes them meaningful.

        var enqueued = _dispatcher.TryEnqueue(async () =>
        {
            string? text = null;
            try
            {
                text = await _service.HandleReadAsync(managedKind);
            }
            catch (Exception ex)
            {
                _logger.LogReadHandlerFailed(ex);
            }
            finally
            {
                if (_isSurfaceAlive(surface))
                    CompleteWithText(surface, state, text, confirmed: false, remember: false);
            }
        });

        if (!enqueued)
        {
            // Dispatcher shutting down. Discharge the request synchronously
            // so libghostty does not leak the state.
            if (_isSurfaceAlive(surface))
                NativeMethods.SurfaceDenyClipboardRequest(surface, state);
        }

        return GhosttyClipboardReadResult.Started;
    }

    // confirm_read_clipboard_cb

    public void HandleConfirm(IntPtr userdata, IntPtr confirmPtr, IntPtr state, GhosttyClipboardRequest request)
    {
        var surface = _resolveSurface(userdata);
        if (surface == IntPtr.Zero)
            return;

        // CRITICAL: copy everything the struct points at before the callback
        // returns. libghostty owns the whole graph for this call only.
        var snapshot = ClipboardConfirmMarshaller.Read(confirmPtr);
        var managedRequest = (ClipboardConfirmRequest)request;

        var enqueued = _dispatcher.TryEnqueue(async () =>
        {
            bool confirmed = false;
            try
            {
                // Until the dialog is widened to show the full payload, the
                // preview is still the text representation. What changes here
                // is only that a refusal is now a denial rather than a
                // completion carrying confirmed = false.
                confirmed = await _service.HandleConfirmAsync(
                    snapshot.PreviewText, managedRequest, surface);
            }
            catch (Exception ex)
            {
                _logger.LogConfirmHandlerFailed(ex);
            }
            finally
            {
                if (_isSurfaceAlive(surface))
                {
                    if (confirmed)
                    {
                        using var complete = new NativeClipboardComplete(
                            snapshot.Contents, snapshot.Available, confirmed: true, remember: false);
                        NativeMethods.SurfaceCompleteClipboardRequest(surface, complete.Pointer, state);
                    }
                    else
                    {
                        NativeMethods.SurfaceDenyClipboardRequest(surface, state);
                    }
                }
            }
        });

        if (!enqueued && _isSurfaceAlive(surface))
            NativeMethods.SurfaceDenyClipboardRequest(surface, state);
    }

    // Completes a read with a single text/plain representation, or denies
    // when there was nothing to give. Completing with an empty payload and
    // denying are different answers now that `confirmed` lives inside the
    // struct, and libghostty distinguishes them.
    private static void CompleteWithText(
        IntPtr surface,
        IntPtr state,
        string? text,
        bool confirmed,
        bool remember)
    {
        if (text is null)
        {
            // Nothing on the clipboard we can serve. Denying and completing
            // with an empty payload are different answers to libghostty now
            // that `confirmed` lives inside the struct.
            NativeMethods.SurfaceDenyClipboardRequest(surface, state);
            return;
        }

        // `available` is what the clipboard is offering, NOT what the caller
        // asked for. Until the backend can enumerate formats, text/plain is
        // the honest answer: it is the one representation this path can
        // actually produce.
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextPlain, text) };
        var available = new[] { ClipboardMime.TextPlain };

        using var complete = new NativeClipboardComplete(payloads, available, confirmed, remember);
        NativeMethods.SurfaceCompleteClipboardRequest(surface, complete.Pointer, state);
    }

    // write_clipboard_cb

    public void HandleWrite(IntPtr userdata, GhosttyClipboard kind, IntPtr content, UIntPtr count, bool confirm)
    {
        var surface = _resolveSurface(userdata);
        if (surface == IntPtr.Zero)
            return;

        var managedKind = (ClipboardKind)kind;
        if (managedKind is ClipboardKind.Selection or ClipboardKind.Primary)
            return;

        // Walk the array WHILE STILL ON THE CALLER'S THREAD. The native
        // memory may be freed once the callback returns.
        var payloads = ClipboardContentMarshaller.Read(content, (nuint)count);
        if (payloads.Count == 0)
            return;

        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await _service.HandleWriteAsync(managedKind, payloads, confirm, surface);
            }
            catch (Exception ex)
            {
                _logger.LogWriteHandlerFailed(ex);
            }
        });
    }
}

internal static partial class ClipboardBridgeLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.ReadHandlerErr,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] read handler failed")]
    internal static partial void LogReadHandlerFailed(
        this ILogger<ClipboardBridge> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.ConfirmHandlerErr,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] confirm handler failed")]
    internal static partial void LogConfirmHandlerFailed(
        this ILogger<ClipboardBridge> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.WriteHandlerErr,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] write handler failed")]
    internal static partial void LogWriteHandlerFailed(
        this ILogger<ClipboardBridge> logger, System.Exception ex);
}
