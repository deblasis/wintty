using System;
using System.IO;
using SkiaSharp;
using Svg.Skia;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Rasterizes a sanitized SVG payload to a square PNG at <paramref name="sizePx"/> pixels.
/// Returns an empty array on any parse/render failure; callers fall back to the bundled default.
/// </summary>
public static class SvgRasterizer
{
    public static byte[] Rasterize(string svgText, int sizePx)
    {
        if (sizePx <= 0) return Array.Empty<byte>();
        var clean = SvgSanitizer.Sanitize(svgText);
        if (string.IsNullOrEmpty(clean)) return Array.Empty<byte>();

        try
        {
            // Svg.Skia 3.x: the parsed SKPicture is exposed via the .Picture
            // property; the Load() return value is for chaining/null-check only.
            // Use FromSvg(string) directly to skip the MemoryStream dance.
            using var svg = new SKSvg();
            if (svg.FromSvg(clean) is null || svg.Picture is null) return Array.Empty<byte>();

            var srcRect = svg.Picture.CullRect;
            if (srcRect.Width <= 0 || srcRect.Height <= 0) return Array.Empty<byte>();

            var info = new SKImageInfo(sizePx, sizePx, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(sizePx / srcRect.Width, sizePx / srcRect.Height);
            canvas.Scale(scale, scale);
            canvas.DrawPicture(svg.Picture);
            canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}
