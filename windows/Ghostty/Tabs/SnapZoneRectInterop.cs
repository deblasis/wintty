using Ghostty.Core.Tabs;
using Windows.Graphics;

namespace Ghostty.Tabs;

/// <summary>
/// Adapts the WinUI-free <see cref="SnapZoneRect"/> to
/// <see cref="RectInt32"/>, the type required by
/// <see cref="Microsoft.UI.Windowing.AppWindow.MoveAndResize"/>.
///
/// Lives in the WinUI project (not Ghostty.Core) so Ghostty.Core
/// does not have to reference the WindowsAppSDK.
/// </summary>
internal static class SnapZoneRectInterop
{
    public static RectInt32 ToRectInt32(this SnapZoneRect r) =>
        new(r.X, r.Y, r.Width, r.Height);
}
