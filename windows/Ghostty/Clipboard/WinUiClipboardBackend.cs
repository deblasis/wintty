using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
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

    /// <summary>
    /// The formats we serve, in preference order, most specific first. A
    /// caller with no filter takes the first thing it can use, and handing
    /// it text/plain for a copied file would lose the paths.
    ///
    /// ONE table, deliberately. Enumerating and reading used to be two
    /// hand-maintained lists, and they drifted the moment image/png was
    /// added to the first and not the second: the clipboard advertised a
    /// representation it would then never produce, so a caller that asked
    /// for the image got an empty answer and no error. Driving both from
    /// this array makes that particular mistake unrepresentable rather than
    /// merely detectable -- adding a row adds it to both, and the reader
    /// switch below has to grow a case or the compiler is no help but the
    /// consistency test is.
    /// </summary>
    private static readonly (string Mime, string Format)[] Served =
    {
        (ClipboardMime.TextUriList, StandardDataFormats.StorageItems),
        (ClipboardMime.ImagePng, StandardDataFormats.Bitmap),
        (ClipboardMime.TextHtml, StandardDataFormats.Html),
        (ClipboardMime.TextPlain, StandardDataFormats.Text),
    };

    /// <summary>The MIME names this backend can ever serve, for tests.</summary>
    internal static IReadOnlyList<string> ServedMimes =>
        Array.ConvertAll(Served, e => e.Mime);

    public async ValueTask<IReadOnlyList<string>> GetAvailableMimesAsync()
    {
        try
        {
            var view = WinClipboard.GetContent();
            var mimes = new List<string>();
            foreach (var (mime, format) in Served)
            {
                if (view.Contains(format)) mimes.Add(mime);
            }

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

        // Same table, same order. Each representation is read independently
        // and a failure on one does not sink the others: the clipboard can
        // offer a format whose source process has since died, and losing the
        // text because the bitmap went away would be the wrong trade.
        foreach (var (mime, format) in Served)
        {
            if (!Wants(wanted, mime)) continue;
            if (!view.Contains(format)) continue;

            var payload = await ReadOneAsync(view, mime);
            if (payload is not null) results.Add(payload.Value);
        }

        return results;
    }

    /// <summary>
    /// Reads one representation. A MIME in <see cref="Served"/> with no case
    /// here would advertise a format we cannot produce, which is the exact
    /// drift the single table exists to prevent, so the default logs rather
    /// than silently returning null.
    /// </summary>
    private async ValueTask<ClipboardPayload?> ReadOneAsync(DataPackageView view, string mime)
    {
        switch (mime)
        {
            case ClipboardMime.TextUriList:
            {
                var uriList = await TryReadUriListAsync(view);
                return uriList is null ? null : ClipboardPayload.FromText(mime, uriList);
            }

            case ClipboardMime.ImagePng:
            {
                var png = await TryReadBitmapAsPngAsync(view);
                return png is null ? null : new ClipboardPayload(mime, png);
            }

            case ClipboardMime.TextHtml:
            {
                var html = await TryReadAsync(() => view.GetHtmlFormatAsync().AsTask());
                return html is null ? null : ClipboardPayload.FromText(mime, html);
            }

            case ClipboardMime.TextPlain:
            {
                var text = await TryReadAsync(() => view.GetTextAsync().AsTask());
                return text is null ? null : ClipboardPayload.FromText(mime, text);
            }

            default:
                _logger.LogUnreadableServedMime(mime);
                return null;
        }
    }

    /// <summary>
    /// Reads the clipboard bitmap and returns PNG bytes.
    ///
    /// The clipboard hands back a stream in whatever format the source app
    /// put there, so the bytes are re-encoded unless they already are PNG.
    /// Serving arbitrary bytes under an image/png label would be a lie the
    /// receiving end cannot detect, and the permission prompt renders this
    /// payload as an image before the user approves anything.
    /// </summary>
    private async ValueTask<byte[]?> TryReadBitmapAsPngAsync(DataPackageView view)
    {
        try
        {
            var reference = await view.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();

            if (string.Equals(source.ContentType, ClipboardMime.ImagePng, StringComparison.OrdinalIgnoreCase))
                return await ReadAllBytesAsync(source);

            var decoder = await BitmapDecoder.CreateAsync(source);
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var target = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, target);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                decoder.PixelWidth,
                decoder.PixelHeight,
                decoder.DpiX,
                decoder.DpiY,
                pixels.DetachPixelData());
            await encoder.FlushAsync();

            target.Seek(0);
            return await ReadAllBytesAsync(target);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            // A bitmap the clipboard advertises but cannot hand over is
            // routine: the source process may have exited. Omitting the
            // representation is right; a zero-byte image/png would render as
            // a broken box in the permission prompt.
            _logger.LogReadFailed(ex, ex is COMException com ? com.HResult : 0);
            return null;
        }
    }

    private static async ValueTask<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        var size = checked((uint)stream.Size);
        var buffer = new Windows.Storage.Streams.Buffer(size);
        await stream.ReadAsync(buffer, size, InputStreamOptions.None);
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
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

    public async ValueTask WriteAsync(IReadOnlyList<ClipboardPayload> payloads)
    {
        DataPackage package;
        try
        {
            package = await BuildPackageAsync(payloads);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or NotSupportedException)
        {
            // Building the package can fail on the image path (a malformed
            // PNG from the wire). Nothing has touched the clipboard yet, so
            // the right move is to leave it alone rather than write a
            // partial package over what the user had.
            _logger.LogWriteFailed(ex, ex is COMException com ? com.HResult : 0);
            return;
        }

        // An empty package would CLEAR the clipboard. Reaching here with one
        // means every representation was dropped, and silently wiping the
        // user's clipboard is far worse than declining the write.
        if (!package.GetView().AvailableFormats.Any())
        {
            _logger.LogWriteProducedNothing(payloads.Count);
            return;
        }

        try
        {
            WinClipboard.SetContent(package);
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
                _dispatcher.TryEnqueue(async () =>
                {
                    try { WinClipboard.SetContent(await BuildPackageAsync(payloads)); }
                    catch (Exception retryEx) when (retryEx is COMException or ArgumentException)
                    {
                        _logger.LogWriteRetryFailed(retryEx,
                            retryEx is COMException rc ? rc.HResult : 0);
                    }
                });
            }
        }
    }

    private static async ValueTask<DataPackage> BuildPackageAsync(IReadOnlyList<ClipboardPayload> payloads)
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
                case WindowsClipboardFormat.Image:
                    package.SetBitmap(await ToStreamReferenceAsync(payload.Data));
                    break;
                default:
                    // Unknown MIME: already filtered by the service, but
                    // be defensive in case the contract drifts.
                    break;
            }
        }

        return package;
    }

    /// <summary>
    /// Wraps PNG bytes as something SetBitmap accepts.
    ///
    /// The stream is written and rewound before the reference is taken:
    /// SetBitmap reads from the current position, and handing it a stream
    /// sitting at the end produces a package that reports a bitmap format
    /// and yields zero bytes -- which looks exactly like a successful write
    /// until someone tries to read it back.
    /// </summary>
    private static async ValueTask<RandomAccessStreamReference> ToStreamReferenceAsync(
        ReadOnlyMemory<byte> png)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(png.ToArray());
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);

        return RandomAccessStreamReference.CreateFromStream(stream);
    }

}

internal static partial class WinUiClipboardBackendLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.ServedMimeNoReader,
                   Level = LogLevel.Error,
                   Message = "[clipboard] {Mime} is advertised but has no reader; the served table and ReadOneAsync have drifted")]
    internal static partial void LogUnreadableServedMime(
        this ILogger<WinUiClipboardBackend> logger, string mime);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.WriteWroteNothing,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] write of {Count} payload(s) produced no writable format; clipboard left untouched")]
    internal static partial void LogWriteProducedNothing(
        this ILogger<WinUiClipboardBackend> logger, int count);

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
