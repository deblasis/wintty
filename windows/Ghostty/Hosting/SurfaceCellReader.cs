using Ghostty.Core.Tabs;
using Ghostty.Interop;

namespace Ghostty.Hosting;

/// <summary>
/// Reads a surface's viewport as a resolved-color <see cref="CellGrid"/> for the
/// colored tab preview. Returns null for a dead/zero surface. FFI seam.
/// </summary>
internal static class SurfaceCellReader
{
    public static CellGrid? Read(System.IntPtr surfaceHandle)
    {
        if (surfaceHandle == System.IntPtr.Zero) return null;
        return NativeMethods.SurfaceReadCells(new GhosttySurface(surfaceHandle));
    }
}
