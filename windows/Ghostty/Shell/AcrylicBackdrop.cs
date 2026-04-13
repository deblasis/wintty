using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Shell;

/// <summary>
/// Acrylic window backdrop with configurable tint and luminosity.
/// Used for the "frosted" and "glass" background styles.
/// </summary>
internal sealed partial class AcrylicBackdrop : SystemBackdrop
{
    private Windows.UI.Color _tintColor;
    private float _tintOpacity;
    private float _luminosityOpacity;

    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;

    /// <param name="tintColor">Tint overlay color.</param>
    /// <param name="tintOpacity">0.0 = no tint, 1.0 = full tint color.</param>
    /// <param name="luminosityOpacity">0.0 = no luminosity, 1.0 = full luminosity layer.</param>
    internal AcrylicBackdrop(
        Windows.UI.Color tintColor,
        float tintOpacity,
        float luminosityOpacity)
    {
        _tintColor = tintColor;
        _tintOpacity = tintOpacity;
        _luminosityOpacity = luminosityOpacity;
    }

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);

        _controller = new DesktopAcrylicController
        {
            TintColor = _tintColor,
            TintOpacity = _tintOpacity,
            LuminosityOpacity = _luminosityOpacity,
            FallbackColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
        };

        // Keep the backdrop active even when the window loses focus.
        _config = new SystemBackdropConfiguration
        {
            IsInputActive = true,
        };

        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    /// <summary>
    /// Update tuning on the live controller without tearing it down.
    /// </summary>
    internal void UpdateTuning(
        Windows.UI.Color tintColor, float tintOpacity, float luminosityOpacity)
    {
        _tintColor = tintColor;
        _tintOpacity = tintOpacity;
        _luminosityOpacity = luminosityOpacity;

        if (_controller is null) return;
        _controller.TintColor = tintColor;
        _controller.TintOpacity = tintOpacity;
        _controller.LuminosityOpacity = luminosityOpacity;
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);

        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        _config = null;
    }
}
