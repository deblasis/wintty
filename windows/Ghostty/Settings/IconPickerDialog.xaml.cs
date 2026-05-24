using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Ghostty.Core.Profiles;
using Ghostty.Tabs;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Ghostty.Settings;

public sealed partial class IconPickerDialog : ContentDialog
{
    public IconSpec? InitialSpec { get; set; }
    public IconSpec? PickedSpec { get; private set; }

    public ObservableCollection<BundledRow> BundledItems { get; } = new();

    public IconPickerDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The set of keys this picker exposes. Keep in sync with the
        // asset bundle in IconAssets.Source.
        var keys = new[]
        {
            "default",
            "pwsh", "cmd", "bash", "fish", "nu", "zsh", "gitbash",
            "ubuntu", "debian", "alpine", "kali", "fedora", "opensuse", "arch",
        };
        foreach (var k in keys)
        {
            var spec = new IconSpec.BrandKey(k, 32);
            var bytes = TabIconBytesCache.GetBytesSync(spec);
            BitmapImage? bmp = null;
            if (bytes is not null && bytes.Length > 0)
            {
                bmp = new BitmapImage();
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    writer.StoreAsync().GetResults();
                }
                ms.Seek(0);
                bmp.SetSource(ms);
            }
            BundledItems.Add(new BundledRow(k, bmp));
        }
        BundledGrid.ItemsSource = BundledItems;
    }

    private void OnBundledItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BundledRow row)
        {
            PickedSpec = new IconSpec.BrandKey(row.Key, null);
        }
    }

    private void OnMdl2TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(Mdl2Input.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp) && cp > 0)
        {
            Mdl2Preview.Glyph = char.ConvertFromUtf32(cp);
            PickedSpec = new IconSpec.Mdl2Token(cp);
        }
    }

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        // Window.Current is a UWP API that returns null in WinUI 3 desktop
        // apps. The dialog's XamlRoot exposes the AppWindowId of its host
        // window via ContentIslandEnvironment, which Win32Interop can map
        // back to an HWND for the file-picker COM initializer.
        var windowId = XamlRoot.ContentIslandEnvironment.AppWindowId;
        var hwnd = Win32Interop.GetWindowFromWindowId(windowId);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".ico");
        picker.FileTypeFilter.Add(".svg");
        picker.FileTypeFilter.Add(".jpg");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            PickedPathLabel.Text = file.Path;
            PickedSpec = new IconSpec.Path(file.Path);
        }
    }

    // Plain class rather than a positional record: the WinUI 3 XAML
    // compiler emits property setters when generating bindable type
    // info for x:Bind / DataTemplate consumers, and init-only record
    // properties produce CS8852.
    public sealed class BundledRow
    {
        public BundledRow(string key, BitmapImage? previewBitmap)
        {
            Key = key;
            PreviewBitmap = previewBitmap;
        }

        public string Key { get; set; }
        public BitmapImage? PreviewBitmap { get; set; }
    }
}
