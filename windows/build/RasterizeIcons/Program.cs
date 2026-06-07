using SkiaSharp;
using Svg.Skia;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: RasterizeIcons <sourceDir> <outputDir>");
    return 2;
}

var sourceDir = args[0];
var outputDir = args[1];
Directory.CreateDirectory(outputDir);

var sizes = new[] { 16, 24, 32 };
var svgs = Directory.EnumerateFiles(sourceDir, "*.svg", SearchOption.AllDirectories).ToList();
if (svgs.Count == 0)
{
    Console.Error.WriteLine($"warning: no .svg under {sourceDir}");
    return 0;
}

var failures = 0;
foreach (var svgPath in svgs)
{
    var key = Path.GetFileNameWithoutExtension(svgPath);
    foreach (var size in sizes)
    {
        var outPath = Path.Combine(outputDir, $"{key}@{size}.png");
        try
        {
            // Svg.Skia 3.x: Load() does not return the SKPicture; read svg.Picture
            // after loading. Mirror SvgRasterizer.cs by going through FromSvg(string).
            using var svg = new SKSvg();
            var svgText = File.ReadAllText(svgPath);
            if (svg.FromSvg(svgText) is null || svg.Picture is null)
            {
                Console.Error.WriteLine($"failed to load {svgPath}");
                failures++;
                continue;
            }
            var src = svg.Picture.CullRect;
            if (src.Width <= 0 || src.Height <= 0)
            {
                Console.Error.WriteLine($"empty cull rect for {svgPath}");
                failures++;
                continue;
            }
            var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(size / src.Width, size / src.Height);
            canvas.Scale(scale, scale);
            canvas.DrawPicture(svg.Picture);
            canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(outPath);
            data.SaveTo(fs);
            Console.WriteLine($"wrote {outPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"failed {svgPath} @{size}: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
    }
}

return failures == 0 ? 0 : 1;
