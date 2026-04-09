using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DialogClipboardConfirmer(DispatcherQueue dispatcher, Func<IntPtr, XamlRoot?> xamlRootProvider)
    {
        _dispatcher = dispatcher;
        _xamlRootProvider = xamlRootProvider;
    }

    public async ValueTask<bool> ConfirmAsync(string preview, ClipboardConfirmRequest request, IntPtr originSurface)
    {
        // Serialize concurrent dialogs. Auto-deny if the previous
        // dialog hangs around for too long.
        if (!await _gate.WaitAsync(ConcurrentDialogWaitTimeout))
            return false;

        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var enqueued = _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var xamlRoot = _xamlRootProvider(originSurface);
                    if (xamlRoot is null)
                    {
                        tcs.TrySetResult(false);
                        return;
                    }

                    var (title, body) = LabelsFor(request);
                    var dialog = new ContentDialog
                    {
                        Title = title,
                        Content = new StackPanel
                        {
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = body,
                                    TextWrapping = TextWrapping.Wrap,
                                    Margin = new Thickness(0, 0, 0, 12),
                                },
                                new ScrollViewer
                                {
                                    MaxHeight = 200,
                                    Content = new TextBlock
                                    {
                                        Text = preview,
                                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
                                        FontSize = 12,
                                        IsTextSelectionEnabled = true,
                                        TextWrapping = TextWrapping.Wrap,
                                    },
                                },
                            },
                        },
                        PrimaryButtonText = "Allow",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close, // Safety default: Cancel
                        XamlRoot = xamlRoot,
                    };

                    var result = await dialog.ShowAsync();
                    tcs.TrySetResult(result == ContentDialogResult.Primary);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[clipboard] confirm dialog failed: {ex.Message}");
                    tcs.TrySetResult(false);
                }
            });

            if (!enqueued)
                return false;

            return await tcs.Task;
        }
        finally
        {
            _gate.Release();
        }
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
        _ => ("Clipboard", "Confirm clipboard access."),
    };
}
