using System.Diagnostics;
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

    // Logged once per instance the first time the broken base hook
    // fires, so when a future WinUI 3 update changes that behaviour we
    // can tell at a glance (the trace will either stop appearing or
    // start appearing with a valid target) whether the override is
    // still load-bearing.
    private bool _defaultConfigWarnedOnce;

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

    /// <summary>
    /// WinUI 3 invokes this when it wants subclasses to refresh the
    /// default <see cref="SystemBackdropConfiguration"/> (typically on
    /// XamlRoot theme transitions). The base implementation forwards
    /// the target to native and throws <c>ArgumentException("parameter
    /// is incorrect")</c> if the target isn't a valid backdrop
    /// composition root -- which is the case when this fires before
    /// <see cref="OnTargetConnected"/> has run (e.g. when the backdrop
    /// is assigned during window construction, before the XAML content
    /// is attached and the target is wired up).
    ///
    /// Since we manage our own <see cref="SystemBackdropConfiguration"/>
    /// in <see cref="OnTargetConnected"/> and drive tint/luminosity via
    /// <see cref="UpdateTuning"/> from config reload, we don't need the
    /// base class's default-config plumbing. Skipping it avoids the
    /// crash and is safe: our controller is reconfigured whenever the
    /// app state that actually matters to us changes.
    ///
    /// We intentionally do not react to XamlRoot theme transitions here
    /// either -- our tint color and luminosity come from the user's
    /// config (tint color, tint opacity, luminosity opacity) not from
    /// system theme resources. A theme change does not change what the
    /// acrylic should look like.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        // Intentionally do not call base. See remarks above.
        if (!_defaultConfigWarnedOnce)
        {
            _defaultConfigWarnedOnce = true;
            Debug.WriteLine(
                "[AcrylicBackdrop] OnDefaultSystemBackdropConfigurationChanged fired; "
              + $"controllerConnected={_controller is not null}, targetNull={target is null}. "
                + "The override is suppressing the base impl to avoid the WinUI 3 ArgumentException.");
        }
    }
}
