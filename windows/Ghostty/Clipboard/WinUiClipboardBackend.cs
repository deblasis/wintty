using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Ghostty.Clipboard;

// TODO(logging): replace Debug.WriteLine with ILogger<T> once the
// Windows port has structured logging infrastructure. Debug.WriteLine
// disappears in Release builds, so production failures here are
// currently invisible.

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

    public WinUiClipboardBackend(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
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
            Debug.WriteLine($"[clipboard] read failed: 0x{ex.HResult:X8}");
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
            Debug.WriteLine($"[clipboard] write failed: 0x{ex.HResult:X8}");

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
                        Debug.WriteLine($"[clipboard] write retry failed: 0x{retryEx.HResult:X8}");
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
