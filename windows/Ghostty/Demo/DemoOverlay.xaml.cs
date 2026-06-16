#if DEMO
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Ghostty.Demo;

/// <summary>
/// Lower-third caption overlay shown during a demo. A tilted Wintty logo sits
/// beside the caption text; an optional "n / total" step indicator appears in
/// stepped mode. Hit-test invisible so the demo's own input is never blocked.
/// </summary>
internal sealed partial class DemoOverlay : UserControl
{
    public DemoOverlay()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadLogoAsync();
    }

    /// <summary>Show a caption, optionally with a step indicator (stepped mode).</summary>
    public void ShowCaption(string text, int? stepIndex = null, int? stepTotal = null)
    {
        CaptionText.Text = text;
        if (stepIndex is int i && stepTotal is int n)
        {
            StepText.Text = $"{i} / {n}";
            StepText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
        else
        {
            StepText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        FadeTo(1, 0);
    }

    /// <summary>Hide the caption pill.</summary>
    public void Hide() => FadeTo(0, 12);

    private void FadeTo(double opacity, double slideY)
    {
        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(180),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, CaptionPill);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        var slide = new DoubleAnimation
        {
            To = slideY,
            Duration = TimeSpan.FromMilliseconds(180),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, PillSlide);
        Storyboard.SetTargetProperty(slide, "Y");
        sb.Children.Add(slide);

        sb.Begin();
    }

    // The logo is embedded in Ghostty.Core as Ghostty.Core.Branding.wintty_logo.png
    // (see Ghostty.Core.csproj). Load it from that assembly's manifest so the
    // overlay reuses the existing brand asset instead of bundling a copy.
    // BitmapImage cannot read a managed Stream directly, hence the
    // DataWriter/StoreAsync -> SetSourceAsync dance. Must stay fully async:
    // blocking SetSourceAsync on the UI thread (.GetAwaiter().GetResult())
    // deadlocks, since the decode marshals back to the UI thread.
    private async Task LoadLogoAsync()
    {
        var asm = typeof(Ghostty.Core.Version.KittyLogo).Assembly;
        using var stream = asm.GetManifestResourceStream("Ghostty.Core.Branding.wintty_logo.png");
        if (stream is null) return;

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var bitmap = new BitmapImage();
        using var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        await bitmap.SetSourceAsync(ras);

        LogoImage.Source = bitmap;
    }
}
#endif
