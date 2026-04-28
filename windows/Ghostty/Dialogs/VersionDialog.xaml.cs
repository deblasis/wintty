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
    private readonly string _output;
    private readonly string _copyButtonRest;
    private bool _copyInProgress;

    public VersionDialog()
    {
        InitializeComponent();

        var info = VersionRenderer.Build();
        // Clipboard payload is the full text (with header + URL line) so the
        // bug-report use case still has everything when pasted elsewhere.
        _output = VersionRenderer.RenderPlain(info);
        // The dialog itself shows the body without the header/URL line --
        // the title bar carries "Wintty <version>" and the URL is rendered
        // as a clickable HyperlinkButton above the body.
        VersionText.Text = VersionRenderer.RenderPlainBody(info);

        // Title bar: app icon + "Wintty <version>". Icon URI is the
        // canonical AppIconSource so packaging changes propagate here.
        TitleIcon.Source = new BitmapImage(AppIconSource.Current);
        TitleText.Text = $"Wintty {info.WinttyVersionString}";

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

        var data = new DataPackage();
        data.SetText(_output);
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
