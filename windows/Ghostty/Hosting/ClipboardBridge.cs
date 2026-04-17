using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Ghostty.Interop;
using Ghostty.Logging;
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

    public ClipboardBridge(
        DispatcherQueue dispatcher,
        ClipboardService service,
        Func<IntPtr, IntPtr> resolveSurface,
        Func<IntPtr, bool> isSurfaceAlive)
    {
        _dispatcher = dispatcher;
        _service = service;
        _resolveSurface = resolveSurface;
        _isSurfaceAlive = isSurfaceAlive;
    }

    // read_clipboard_cb

    public bool HandleRead(IntPtr userdata, GhosttyClipboard kind, IntPtr state)
    {
        var surface = _resolveSurface(userdata);
        if (surface == IntPtr.Zero)
            return false;

        var managedKind = (ClipboardKind)kind;
        if (managedKind == ClipboardKind.Selection)
            return false;

        var enqueued = _dispatcher.TryEnqueue(async () =>
        {
            string textToReturn = string.Empty;
            try
            {
                var text = await _service.HandleReadAsync(managedKind);
                textToReturn = text ?? string.Empty;
            }
            catch (Exception ex)
            {
                StaticLoggers.ClipboardBridge.LogReadHandlerFailed(ex);
            }
            finally
            {
                if (_isSurfaceAlive(surface))
                {
                    NativeMethods.SurfaceCompleteClipboardRequest(
                        surface, textToReturn, state, false);
                }
            }
        });

        if (!enqueued)
        {
            // Dispatcher shutting down. Complete synchronously so
            // libghostty does not leak the request state.
            if (_isSurfaceAlive(surface))
            {
                NativeMethods.SurfaceCompleteClipboardRequest(
                    surface, string.Empty, state, false);
            }
        }

        return true;
    }

    // confirm_read_clipboard_cb

    public void HandleConfirm(IntPtr userdata, IntPtr str, IntPtr state, GhosttyClipboardRequest request)
    {
        var surface = _resolveSurface(userdata);
        if (surface == IntPtr.Zero)
            return;

        // CRITICAL: copy the C string before the callback returns.
        // libghostty owns the buffer for the duration of this call only.
        var text = Marshal.PtrToStringUTF8(str) ?? string.Empty;
        var managedRequest = (ClipboardConfirmRequest)request;

        var enqueued = _dispatcher.TryEnqueue(async () =>
        {
            bool confirmed = false;
            try
            {
                confirmed = await _service.HandleConfirmAsync(text, managedRequest, surface);
            }
            catch (Exception ex)
            {
                StaticLoggers.ClipboardBridge.LogConfirmHandlerFailed(ex);
            }
            finally
            {
                if (_isSurfaceAlive(surface))
                {
                    NativeMethods.SurfaceCompleteClipboardRequest(
                        surface, text, state, confirmed);
                }
            }
        });

        if (!enqueued && _isSurfaceAlive(surface))
        {
            NativeMethods.SurfaceCompleteClipboardRequest(
                surface, text, state, false);
        }
    }

    // write_clipboard_cb

    public void HandleWrite(IntPtr userdata, GhosttyClipboard kind, IntPtr content, UIntPtr count, bool confirm)
    {
        var surface = _resolveSurface(userdata);
        if (surface == IntPtr.Zero)
            return;

        var managedKind = (ClipboardKind)kind;
        if (managedKind == ClipboardKind.Selection)
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
                StaticLoggers.ClipboardBridge.LogWriteHandlerFailed(ex);
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
