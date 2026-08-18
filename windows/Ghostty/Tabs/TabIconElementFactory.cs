using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Ghostty.Tabs;

/// <summary>
/// Builds WinUI <see cref="IconElement"/> for
/// <see cref="NavigationViewItem.Icon"/> from core tab icon VMs.
/// </summary>
internal static class TabIconElementFactory
{
    internal static IconElement? Create(TabIconViewModel? vm)
    {
        if (vm is null) return null;

        return vm.Icon switch
        {
            IconSpec.Mdl2Token mdl2 => BuildFontIcon(mdl2.CodePoint),
            _ => BuildImageIcon(vm.Icon),
        };
    }

    private static FontIcon BuildFontIcon(int codePoint)
    {
        var fi = new FontIcon { FontSize = 16 };
        if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var ff)
            && ff is Microsoft.UI.Xaml.Media.FontFamily fam)
        {
            fi.FontFamily = fam;
        }
        if (codePoint > 0)
            fi.Glyph = char.ConvertFromUtf32(codePoint);
        return fi;
    }

    private static IconElement BuildImageIcon(IconSpec spec)
    {
        var icon = new ImageIcon { Width = 20, Height = 20 };
        var bytes = TabIconBytesCache.GetBytesSync(spec);
        if (bytes is not { Length: > 0 }) return icon;

        var bmp = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().GetResults();
        }
        stream.Seek(0);
        bmp.SetSource(stream);
        icon.Source = bmp;
        return icon;
    }
}
