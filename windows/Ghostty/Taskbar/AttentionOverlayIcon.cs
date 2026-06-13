using System;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Taskbar;

/// <summary>
/// Builds a small filled-dot HICON for the taskbar attention overlay,
/// sized to the system small-icon metrics. Zero external assets: the
/// pixels are rasterized into a 32-bpp ARGB DIB section at runtime.
/// Caller owns the returned HICON and must <c>DestroyIcon</c> it.
/// </summary>
internal static class AttentionOverlayIcon
{
    // Opaque accent (early-sunrise amber). Written premultiplied below.
    private const byte ColorR = 0xFF, ColorG = 0x8A, ColorB = 0x1E;

    public static unsafe HICON Create()
    {
        int w = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        int h = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON);
        if (w <= 0) w = 16;
        if (h <= 0) h = 16;

        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = w,
            bV5Height = -h,                       // negative => top-down rows
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BI_COMPRESSION.BI_BITFIELDS,
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };

        void* bits;
        HDC screen = PInvoke.GetDC(default);
        HBITMAP color;
        try
        {
            color = PInvoke.CreateDIBSection(
                screen, (BITMAPINFO*)&header, DIB_USAGE.DIB_RGB_COLORS,
                out bits, default, 0);
        }
        finally
        {
            PInvoke.ReleaseDC(default, screen);
        }
        if (color.IsNull) return default;
        if (bits is null)
        {
            // CreateDIBSection never hands back a live handle with null
            // bits in practice, but free it rather than leak the section
            // if it ever did.
            PInvoke.DeleteObject(color);
            return default;
        }

        RasterizeDot((byte*)bits, w, h);

        // CreateIconIndirect needs a mask bitmap even for a 32-bpp alpha
        // color plane; an all-zero monochrome mask leaves the alpha in
        // charge of the shape.
        HBITMAP mask = PInvoke.CreateBitmap(w, h, 1, 1, null);
        var info = new ICONINFO
        {
            fIcon = true,
            hbmMask = mask,
            hbmColor = color,
        };
        HICON icon = PInvoke.CreateIconIndirect(in info);

        // CreateIconIndirect copies the bitmaps; free our originals.
        if (!color.IsNull) PInvoke.DeleteObject(color);
        if (!mask.IsNull) PInvoke.DeleteObject(mask);
        return icon;
    }

    // Fill a centered, edge-feathered disc into a top-down BGRA buffer
    // with premultiplied alpha (CreateIconIndirect honors the alpha).
    private static unsafe void RasterizeDot(byte* p, int w, int h)
    {
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double radius = (w < h ? w : h) / 2.0 - 0.5;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx, dy = y - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                // 1px feather at the edge for anti-aliasing.
                double cover = radius - d;
                double a = cover >= 1 ? 1.0 : cover <= 0 ? 0.0 : cover;
                byte* px = p + (y * w + x) * 4;
                px[0] = (byte)(ColorB * a);    // blue (premultiplied)
                px[1] = (byte)(ColorG * a);    // green
                px[2] = (byte)(ColorR * a);    // red
                px[3] = (byte)(0xFF * a);      // alpha
            }
        }
    }
}
