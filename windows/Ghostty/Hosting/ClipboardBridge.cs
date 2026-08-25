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

        // Copy the MIME filter WHILE STILL ON THE CALLER'S THREAD; the array
        // belongs to libghostty for the duration of this call.
        var accepted = ClipboardConfirmMarshaller.ReadStringArray(mimeFilter, mimeFilterLen);

        var enqueued = _dispatcher.TryEnqueue(async () =>
        {
            ClipboardReadOutcome outcome = ClipboardReadOutcome.Empty;
            try
            {
                outcome = await _service.HandleKittyReadAsync(managedKind, accepted, listing);
            }
            catch (Exception ex)
            {
                _logger.LogReadHandlerFailed(ex);
            }
            finally
            {
                if (_isSurfaceAlive(surface))
                    Complete(surface, state, outcome, confirmed: false, remember: false);
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
            var decision = ClipboardConfirmResult.Denied;
            try
            {
                decision = await _service.HandleConfirmAsync(snapshot, managedRequest, surface);
            }
            catch (Exception ex)
            {
                _logger.LogConfirmHandlerFailed(ex);
            }
            finally
            {
                if (_isSurfaceAlive(surface))
                {
                    if (decision.Accepted)
                    {
                        // The contents come back from the snapshot, not from a
                        // fresh clipboard read: what the user approved is what
                        // they were shown. Re-reading here would open a window
                        // in which the clipboard changes between the prompt and
                        // the answer.
                        using var complete = new NativeClipboardComplete(
                            snapshot.Contents,
                            snapshot.Available,
                            confirmed: true,
                            decision.Remember && snapshot.CanRemember);
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
    // Completes a read, or denies it when there is nothing to give.
    // Completing with an empty payload and denying are different answers
    // now that `confirmed` lives inside the struct, and libghostty
    // distinguishes them.
    private static void Complete(
        IntPtr surface,
        IntPtr state,
        ClipboardReadOutcome outcome,
        bool confirmed,
        bool remember)
    {
        if (outcome.Status != ClipboardReadStatus.Ok)
        {
            NativeMethods.SurfaceDenyClipboardRequest(surface, state);
            return;
        }

        // `available` is what the clipboard is offering, NOT what the caller
        // asked for. A caller that filtered to image/png still wants to be
        // told text/plain exists, which is the whole point of the LIST
        // request being answerable without reading anything.
        using var complete = new NativeClipboardComplete(
            outcome.Contents, outcome.Available, confirmed, remember);
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
