using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Ghostty.Clipboard;

/// <summary>
/// Production IClipboardBackend backed by Windows.ApplicationModel.
/// DataTransfer.Clipboard. Must be called from the UI thread; the
/// bridge dispatches all calls before invoking us.
/// </summary>
internal sealed class WinUiClipboardBackend : IClipboardBackend
{
    // CO_E_NOTINITIALIZED is the WinUI 3 startup race when SetContent is
    // called before the window's clipboard broker is fully ready.
    // See memory/reference_winui3_quirks.md.
    private const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<WinUiClipboardBackend> _logger;

    public WinUiClipboardBackend(DispatcherQueue dispatcher, ILogger<WinUiClipboardBackend> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async ValueTask<string?> ReadTextAsync()
    {
        try
        {
            var view = WinClipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
                return null;
            return await view.GetTextAsync();
        }
        catch (COMException ex)
        {
            // Clipboard locked by another process. Treated as "no text".
            _logger.LogReadFailed(ex, ex.HResult);
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<string>> GetAvailableMimesAsync()
    {
        try
        {
            var view = WinClipboard.GetContent();
            var mimes = new List<string>();

            // Order is preference order, most specific first. A caller with
            // no filter takes the first thing it can use, and handing it
            // text/plain for a copied file would lose the paths.
            if (view.Contains(StandardDataFormats.StorageItems))
                mimes.Add(ClipboardMime.TextUriList);
            if (view.Contains(StandardDataFormats.Html))
                mimes.Add(ClipboardMime.TextHtml);
            if (view.Contains(StandardDataFormats.Bitmap))
                mimes.Add(ClipboardMime.ImagePng);
            if (view.Contains(StandardDataFormats.Text))
                mimes.Add(ClipboardMime.TextPlain);

            return mimes;
        }
        catch (COMException ex)
        {
            _logger.LogReadFailed(ex, ex.HResult);
            return Array.Empty<string>();
        }
        finally
        {
            await ValueTask.CompletedTask;
        }
    }

    public async ValueTask<IReadOnlyList<ClipboardPayload>> ReadAsync(IReadOnlyList<string> accepted)
    {
        DataPackageView view;
        try
        {
            view = WinClipboard.GetContent();
        }
        catch (COMException ex)
        {
            _logger.LogReadFailed(ex, ex.HResult);
            return Array.Empty<ClipboardPayload>();
        }

        var wanted = BuildFilter(accepted);
        var results = new List<ClipboardPayload>();

        // Each representation is read independently and a failure on one
        // does not sink the others: the clipboard can offer a format whose
        // source process has since died, and losing the text because the
        // bitmap went away would be the wrong trade.
        if (Wants(wanted, ClipboardMime.TextUriList) && view.Contains(StandardDataFormats.StorageItems))
        {
            var uriList = await TryReadUriListAsync(view);
            if (uriList is not null)
                results.Add(ClipboardPayload.FromText(ClipboardMime.TextUriList, uriList));
        }

        if (Wants(wanted, ClipboardMime.TextHtml) && view.Contains(StandardDataFormats.Html))
        {
            var html = await TryReadAsync(() => view.GetHtmlFormatAsync().AsTask());
            if (html is not null)
                results.Add(ClipboardPayload.FromText(ClipboardMime.TextHtml, html));
        }

        if (Wants(wanted, ClipboardMime.TextPlain) && view.Contains(StandardDataFormats.Text))
        {
            var text = await TryReadAsync(() => view.GetTextAsync().AsTask());
            if (text is not null)
                results.Add(ClipboardPayload.FromText(ClipboardMime.TextPlain, text));
        }

        return results;
    }

    /// <summary>
    /// Files copied in Explorer arrive as StorageItems (CF_HDROP). macOS
    /// serves the same thing as text/uri-list; this is that mapping.
    /// </summary>
    private async ValueTask<string?> TryReadUriListAsync(DataPackageView view)
    {
        try
        {
            var items = await view.GetStorageItemsAsync();
            var paths = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
            }

            return UriListFormatter.Format(paths);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogReadFailed(ex, ex is COMException com ? com.HResult : 0);
            return null;
        }
    }

    private async ValueTask<string?> TryReadAsync(Func<Task<string>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogReadFailed(ex, ex is COMException com ? com.HResult : 0);
            return null;
        }
    }

    // An empty filter means "anything"; null marks that case so the
    // per-format checks below do not each have to special-case it.
    private static HashSet<string>? BuildFilter(IReadOnlyList<string> accepted) =>
        accepted.Count == 0 ? null : new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase);

    private static bool Wants(HashSet<string>? filter, string mime) =>
        filter is null || filter.Contains(mime);

    public ValueTask WriteAsync(IReadOnlyList<ClipboardPayload> payloads)
    {
        try
        {
            WinClipboard.SetContent(BuildPackage(payloads));
        }
        catch (COMException ex)
        {
            _logger.LogWriteFailed(ex, ex.HResult);

            // CO_E_NOTINITIALIZED is a known WinUI 3 startup race: the
            // clipboard broker is not ready yet. Retry once on the next
            // dispatcher tick. Other HResults (notably CLIPBRD_E_CANT_OPEN
            // when another process holds the clipboard) are logged and
            // dropped -- there is no useful retry strategy.
            //
            // DataPackage is a single-use transfer object: once handed to
            // SetContent the runtime takes ownership, so the retry must
            // build a fresh package instead of reusing the one that threw.
            if (ex.HResult == CO_E_NOTINITIALIZED)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    try { WinClipboard.SetContent(BuildPackage(payloads)); }
                    catch (COMException retryEx)
                    {
                        _logger.LogWriteRetryFailed(retryEx, retryEx.HResult);
                    }
                });
            }
        }

        return ValueTask.CompletedTask;
    }

    private static DataPackage BuildPackage(IReadOnlyList<ClipboardPayload> payloads)
    {
        var package = new DataPackage();
        foreach (var payload in payloads)
        {
            switch (WindowsClipboardFormatMap.FromMimeForWrite(payload.Mime))
            {
                case WindowsClipboardFormat.Text:
                    package.SetText(payload.Text);
                    break;
                case WindowsClipboardFormat.Html:
                    // CreateHtmlFormat wraps the body in the CF_HTML
                    // header that Word and Outlook understand.
                    package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(payload.Text));
                    break;
                default:
                    // Unknown MIME: already filtered by the service, but
                    // be defensive in case the contract drifts.
                    break;
            }
        }
        return package;
    }
}

internal static partial class WinUiClipboardBackendLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.ReadFailed,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] read failed: 0x{HResult:X8}")]
    internal static partial void LogReadFailed(
        this ILogger<WinUiClipboardBackend> logger, System.Exception ex, int hresult);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.WriteFailed,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] write failed: 0x{HResult:X8}")]
    internal static partial void LogWriteFailed(
        this ILogger<WinUiClipboardBackend> logger, System.Exception ex, int hresult);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.WriteRetryFailed,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] write retry failed: 0x{HResult:X8}")]
    internal static partial void LogWriteRetryFailed(
        this ILogger<WinUiClipboardBackend> logger, System.Exception ex, int hresult);
}
