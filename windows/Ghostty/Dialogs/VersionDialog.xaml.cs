using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Version;
using Microsoft.UI.Xaml.Controls;
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
        _output = VersionRenderer.RenderPlain(info);
        VersionText.Text = _output;
        Title = $"Wintty {info.WinttyVersionString}";

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
