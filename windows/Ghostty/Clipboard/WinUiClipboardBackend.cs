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
            switch (WindowsClipboardFormatMap.FromMime(payload.Mime))
            {
                case WindowsClipboardFormat.Text:
                    package.SetText(payload.Data);
                    break;
                case WindowsClipboardFormat.Html:
                    // CreateHtmlFormat wraps the body in the CF_HTML
                    // header that Word and Outlook understand.
                    package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(payload.Data));
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
