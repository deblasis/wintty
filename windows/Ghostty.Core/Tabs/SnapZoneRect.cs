namespace Ghostty.Core.Tabs;

/// <summary>
/// Pure-int rectangle for SnapZoneMath. Deliberately does NOT
/// reference Windows.Graphics.RectInt32 so Ghostty.Core stays free
/// of the WindowsAppSDK dependency. The WinUI shell converts to
/// RectInt32 at the call site via SnapZoneRectInterop.
/// </summary>
internal readonly record struct SnapZoneRect(int X, int Y, int Width, int Height);
