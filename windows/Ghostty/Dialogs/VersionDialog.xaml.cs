using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Branding;
using Ghostty.Core.Version;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Ghostty.Dialogs;

internal sealed partial class VersionDialog : ContentDialog
{
    private readonly string _copyButtonRest;
    private bool _copyInProgress;

    public VersionDialog()
    {
        InitializeComponent();

        var info = VersionRenderer.Build(Ghostty.Services.AnimationsState.Code());
        // The dialog itself shows the body without the header/URL line --
        // the title bar carries the one-line header and the URL is rendered
        // as a clickable HyperlinkButton above the body. The clipboard
        // payload is NOT taken from this open-time build: identity and
        // commit cannot change while the dialog is open, but the resolved
        // animation state can, so the copy handler composes it fresh.
        VersionText.Text = VersionRenderer.RenderPlainBody(info);

        // Title bar: app icon + the same one-line identity `+version` prints
        // first. Icon URI is the canonical AppIconSource so packaging changes
        // propagate here.
        TitleIcon.Source = new BitmapImage(AppIconSource.Current);
        TitleText.Text = VersionHeader.Compose(info);

        var commitUrl = VersionRenderer.CommitUrl(info);
        if (commitUrl is null)
        {
            CommitLine.Visibility = Visibility.Collapsed;
        }
        else
        {
            CommitLink.NavigateUri = new Uri(commitUrl);
            CommitLinkText.Text = commitUrl;
        }

        // Capture the original button label once so re-entrancy (rapid
        // double-click) can't leave the button stuck on "Copied".
        _copyButtonRest = PrimaryButtonText;
        PrimaryButtonClick += OnCopy;
    }

    private async void OnCopy(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Don't dismiss the dialog when Copy is clicked.
        args.Cancel = true;

        // Drop double-clicks while a previous Copy is still mid-revert.
        if (_copyInProgress) return;
        _copyInProgress = true;

        // Re-resolve at copy time: the payload is the bug-report use case
        // that must state what holds NOW, not what held when the dialog
        // opened. One ask feeds both the paste and the refreshed body, so
        // the display cannot disagree with what lands on the clipboard.
        var dump = Ghostty.Services.AnimationsState.ComposeDump();
        var data = new DataPackage();
        data.SetText(dump.Payload);
        VersionText.Text = dump.Body;
        try
        {
            // SetContent races WinUI startup and can throw CO_E_NOTINITIALIZED /
            // CLIPBRD_E_CANT_OPEN -- same hazard handled in WinUiClipboardBackend.
            WinClipboard.SetContent(data);
        }
        catch (COMException)
        {
            _copyInProgress = false;
            return;
        }

        PrimaryButtonText = "Copied";
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
        }
        finally
        {
            PrimaryButtonText = _copyButtonRest;
            _copyInProgress = false;
        }
    }
}
