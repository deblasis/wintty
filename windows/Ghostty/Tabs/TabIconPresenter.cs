using System;
using System.ComponentModel;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Ghostty.Tabs;

/// <summary>
/// Imperative presenter for a <see cref="TabIconViewModel"/>. Picks
/// between an <see cref="Image"/> (PNG-backed icons) and a
/// <see cref="FontIcon"/> (MDL2 glyph) based on the VM's current spec,
/// and rebuilds the child element when the spec switches.
///
/// We avoid XAML <c>{Binding}</c> here because that requires the Core
/// types (TabModel / TabIconViewModel) to carry
/// <c>[WinRT.GeneratedBindableCustomProperty]</c> under CsWinRT 2.x —
/// which would drag a UI-framework dependency into Ghostty.Core and
/// break the Core/UI split. <c>x:Bind</c> in shared
/// <c>Application.Resources</c> DataTemplates produced a silent
/// XamlCompiler exit-1 in WinUI 3 1.8 (Pass 2 codegen aborts without
/// diagnostics), so we render the icon UI imperatively instead.
/// </summary>
internal sealed partial class TabIconPresenter : ContentControl
{
    private TabIconViewModel? _vm;
    private PropertyChangedEventHandler? _handler;

    public TabIconPresenter()
    {
        IsTabStop = false;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>
    /// Bind this presenter to <paramref name="vm"/>. Any prior VM is
    /// unsubscribed first. Pass <c>null</c> to clear.
    /// </summary>
    public void Attach(TabIconViewModel? vm)
    {
        Detach();
        _vm = vm;
        if (vm is null)
        {
            Content = null;
            ToolTipService.SetToolTip(this, null);
            return;
        }

        _handler = (_, e) =>
        {
            // Rebuild content on Icon switches (which also implies
            // Mdl2CodePoint / IsMdl2Glyph changes), and refresh the
            // tooltip when TooltipText changes.
            if (e.PropertyName is null
                || e.PropertyName == nameof(TabIconViewModel.Icon)
                || e.PropertyName == nameof(TabIconViewModel.IsMdl2Glyph)
                || e.PropertyName == nameof(TabIconViewModel.Mdl2CodePoint))
            {
                Rebuild();
            }
            if (e.PropertyName is null
                || e.PropertyName == nameof(TabIconViewModel.TooltipText))
            {
                ToolTipService.SetToolTip(this, _vm?.TooltipText);
            }
        };
        vm.PropertyChanged += _handler;
        Rebuild();
        ToolTipService.SetToolTip(this, vm.TooltipText);
    }

    private void Detach()
    {
        if (_vm is not null && _handler is not null)
            _vm.PropertyChanged -= _handler;
        _vm = null;
        _handler = null;
    }

    private void Rebuild()
    {
        if (_vm is null) { Content = null; return; }

        Content = _vm.Icon switch
        {
            IconSpec.Mdl2Token mdl2 => BuildFontIcon(mdl2.CodePoint),
            _ => BuildImage(_vm.Icon),
        };
    }

    private static FrameworkElement BuildFontIcon(int codePoint)
    {
        var fi = new FontIcon { FontSize = 16 };
        // Match the SymbolThemeFontFamily theme resource used by the
        // previous XAML template so MDL2 / Fluent glyphs render correctly.
        if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var ff)
            && ff is Microsoft.UI.Xaml.Media.FontFamily fam)
        {
            fi.FontFamily = fam;
        }
        if (codePoint > 0)
            fi.Glyph = char.ConvertFromUtf32(codePoint);
        return fi;
    }

    private static FrameworkElement BuildImage(IconSpec spec)
    {
        var img = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
        var bytes = TabIconBytesCache.GetBytesSync(spec);
        if (bytes is { Length: > 0 })
        {
            var bmp = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().GetResults();
            }
            stream.Seek(0);
            bmp.SetSource(stream);
            img.Source = bmp;
        }
        return img;
    }
}
