using System;

namespace Ghostty.Core.Config;

/// <summary>
/// Pure-logic type that computes what window layers should do based on
/// the background-opacity config value. No Win32 or WinUI dependencies
/// so it can be unit-tested directly.
///
/// Not yet consumed by MainWindow -- this is scaffolding for issue #196
/// (actual window transparency via HWND + DirectComposition). The type
/// and its tests land here so the follow-up PR can wire it in without
/// a large diff.
/// </summary>
public readonly struct WindowTransparencyState : IEquatable<WindowTransparencyState>
{
    /// <summary>
    /// The clamped opacity value (0.0 to 1.0).
    /// </summary>
    public double Opacity { get; }

    /// <summary>
    /// Whether the window should use transparent compositing.
    /// True when opacity is strictly less than 1.0.
    /// </summary>
    public bool IsTransparent { get; }

    /// <summary>
    /// Whether a system backdrop (e.g. Mica) should be applied.
    /// False when transparent -- the backdrop would paint opaque
    /// behind the swap chain and defeat the transparency.
    /// </summary>
    public bool UseSystemBackdrop { get; }

    /// <summary>
    /// Whether DWM glass margins should extend into the client area.
    /// True when transparent -- tells DWM to composite the swap
    /// chain's premultiplied alpha against the desktop.
    /// </summary>
    public bool ExtendDwmGlass { get; }

    /// <summary>
    /// Whether the Win32 class brush should be hollow (transparent).
    /// True when transparent so DWM sees through to the swap chain.
    /// False when opaque -- a dark brush hides resize flicker.
    /// </summary>
    public bool UseHollowClassBrush { get; }

    private WindowTransparencyState(double opacity)
    {
        // Defensive clamp: IConfigService.BackgroundOpacity already
        // guarantees [0,1], but this type must be safe standalone since
        // it is the public API for computing transparency decisions.
        Opacity = Math.Clamp(opacity, 0.0, 1.0);
        IsTransparent = Opacity < 1.0;
        UseSystemBackdrop = !IsTransparent;
        ExtendDwmGlass = IsTransparent;
        UseHollowClassBrush = IsTransparent;
    }

    /// <summary>
    /// Create a transparency state from a raw opacity value.
    /// Values outside [0, 1] are clamped.
    /// </summary>
    public static WindowTransparencyState FromOpacity(double opacity) => new(opacity);

    public bool Equals(WindowTransparencyState other) => Opacity.Equals(other.Opacity);
    public override bool Equals(object? obj) => obj is WindowTransparencyState other && Equals(other);
    public override int GetHashCode() => Opacity.GetHashCode();
    public static bool operator ==(WindowTransparencyState left, WindowTransparencyState right) => left.Equals(right);
    public static bool operator !=(WindowTransparencyState left, WindowTransparencyState right) => !left.Equals(right);
}
