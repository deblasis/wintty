using System;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Ghostty.Clipboard;

/// <summary>
/// Renders the libghostty clipboard confirmation dialog as a WinUI 3
/// ContentDialog. Resolves the active XamlRoot via a callback so the
/// confirmer is independent of which TerminalControl is focused.
///
/// WinUI 3 only allows one ContentDialog per XamlRoot at a time, so
/// concurrent confirmations are serialized via a SemaphoreSlim. If the
/// wait exceeds 30 seconds, the request is auto-denied as the safe
/// default for a security-relevant dialog.
/// </summary>
internal sealed class DialogClipboardConfirmer : IClipboardConfirmer
{
    private static readonly TimeSpan ConcurrentDialogWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<IntPtr, XamlRoot?> _xamlRootProvider;
    private readonly ILogger<DialogClipboardConfirmer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DialogClipboardConfirmer(
        DispatcherQueue dispatcher,
        Func<IntPtr, XamlRoot?> xamlRootProvider,
        ILogger<DialogClipboardConfirmer> logger)
    {
        _dispatcher = dispatcher;
        _xamlRootProvider = xamlRootProvider;
        _logger = logger;
    }

    public async ValueTask<ClipboardConfirmResult> ConfirmAsync(
        ClipboardConfirmSnapshot snapshot,
        ClipboardConfirmRequest request,
        IntPtr originSurface)
    {
        // Serialize concurrent dialogs. Auto-deny if the previous
        // dialog hangs around for too long.
        if (!await _gate.WaitAsync(ConcurrentDialogWaitTimeout))
            return ClipboardConfirmResult.Denied;

        try
        {
            var tcs = new TaskCompletionSource<ClipboardConfirmResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var enqueued = _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var xamlRoot = _xamlRootProvider(originSurface);
                    if (xamlRoot is null)
                    {
                        tcs.TrySetResult(ClipboardConfirmResult.Denied);
                        return;
                    }

                    var (title, body) = LabelsFor(request);
                    var panel = new StackPanel();

                    panel.Children.Add(new TextBlock
                    {
                        Text = body,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 12),
                    });

                    // Who is asking, when libghostty knows. Shown above the
                    // payload: the identity is the part that should decide the
                    // answer, and burying it under a scroll view of text means
                    // it never gets read.
                    if (!string.IsNullOrWhiteSpace(snapshot.Name))
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = snapshot.Name,
                            TextWrapping = TextWrapping.Wrap,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Margin = new Thickness(0, 0, 0, 12),
                        });
                    }

                    AddPreview(panel, snapshot);

                    // The representations on offer, when there is more than the
                    // one being previewed. Allow grants all of them, so all of
                    // them have to be visible.
                    if (snapshot.Available.Count > 1)
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = "Includes: " + string.Join(", ", snapshot.Available),
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.7,
                            FontSize = 12,
                            Margin = new Thickness(0, 12, 0, 0),
                        });
                    }

                    CheckBox? remember = null;
                    if (snapshot.CanRemember)
                    {
                        remember = new CheckBox
                        {
                            Content = "Do not ask again for this session",
                            Margin = new Thickness(0, 12, 0, 0),
                        };
                        panel.Children.Add(remember);
                    }

                    var dialog = new ContentDialog
                    {
                        Title = title,
                        Content = new ScrollViewer { MaxHeight = 360, Content = panel },
                        PrimaryButtonText = "Allow",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close, // Safety default: Cancel
                        XamlRoot = xamlRoot,
                    };

                    var result = await dialog.ShowAsync();
                    var accepted = result == ContentDialogResult.Primary;

                    // Only a granted request can be remembered, and only when
                    // the request said a grant may be offered.
                    tcs.TrySetResult(accepted
                        ? ClipboardConfirmResult.Allow(remember?.IsChecked == true)
                        : ClipboardConfirmResult.Denied);
                }
                catch (Exception ex)
                {
                    _logger.LogConfirmDialogFailed(ex);
                    tcs.TrySetResult(ClipboardConfirmResult.Denied);
                }
            });

            if (!enqueued)
                return ClipboardConfirmResult.Denied;

            return await tcs.Task;
        }
        finally
        {
            _gate.Release();
        }
    }


    /// <summary>
    /// Renders the payload being approved. An image is shown as an image:
    /// decoding image bytes as UTF-8 puts a wall of mojibake in front of a
    /// security decision, which trains people to click Allow without
    /// reading. Anything with no renderable representation says so
    /// explicitly rather than showing an empty box.
    /// </summary>
    private static void AddPreview(StackPanel panel, ClipboardConfirmSnapshot snapshot)
    {
        var image = FindImage(snapshot);
        if (image is not null)
        {
            var source = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(image.Value.Data.ToArray());
                    writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                    writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                    writer.DetachStream();
                }

                stream.Seek(0);
                source.SetSource(stream);
            }

            panel.Children.Add(new Image
            {
                Source = source,
                MaxHeight = 200,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
            return;
        }

        var text = snapshot.PreviewText;
        if (!string.IsNullOrEmpty(text))
        {
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 200,
                Content = new TextBlock
                {
                    Text = text,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
            return;
        }

        panel.Children.Add(new TextBlock
        {
            Text = snapshot.Contents.Count == 0
                ? "The clipboard is empty."
                : "This content cannot be previewed.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static ClipboardPayload? FindImage(ClipboardConfirmSnapshot snapshot)
    {
        foreach (var payload in snapshot.Contents)
        {
            if (payload.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && payload.Data.Length > 0)
            {
                return payload;
            }
        }

        return null;
    }

    private static (string Title, string Body) LabelsFor(ClipboardConfirmRequest request) => request switch
    {
        ClipboardConfirmRequest.Paste => (
            "Paste from clipboard",
            "An application is asking to paste the following text into the terminal."),
        ClipboardConfirmRequest.Osc52Read => (
            "Allow clipboard read",
            "A terminal application is asking to read the contents of your clipboard."),
        ClipboardConfirmRequest.Osc52Write => (
            "Allow clipboard write",
            "A terminal application is asking to write the following text to your clipboard."),
        ClipboardConfirmRequest.KittyRead => (
            "Allow clipboard read",
            "A terminal application is asking to read the contents of your clipboard."),
        ClipboardConfirmRequest.KittyWrite => (
            "Allow clipboard write",
            "A terminal application is asking to write the following to your clipboard."),
        ClipboardConfirmRequest.List => (
            "Allow clipboard listing",
            "A terminal application is asking which formats your clipboard is offering."),
        _ => ("Clipboard", "Confirm clipboard access."),
    };
}

internal static partial class DialogClipboardConfirmerLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Clipboard.ConfirmDialogErr,
                   Level = LogLevel.Warning,
                   Message = "[clipboard] confirm dialog failed")]
    internal static partial void LogConfirmDialogFailed(
        this ILogger<DialogClipboardConfirmer> logger, System.Exception ex);
}
