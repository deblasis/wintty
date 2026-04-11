using System.Drawing;

namespace Ghostty.IconGen;

/// <summary>
/// Loads the macOS-style icon masters from images/icons/icon_*.png.
/// These are the single source of truth for the Windows icon raster;
/// we never read anything from dist/windows/ or duplicate assets.
/// </summary>
internal sealed class MasterRasters : IDisposable
{
    private readonly Dictionary<int, Bitmap> _byPx;

    private MasterRasters(Dictionary<int, Bitmap> byPx)
    {
        _byPx = byPx;
    }

    internal static MasterRasters FromDictionary(Dictionary<int, Bitmap> byPx)
        => new(byPx);

    public IReadOnlyCollection<int> Sizes => _byPx.Keys;

    public Bitmap Get(int px)
    {
        if (!_byPx.TryGetValue(px, out var bitmap))
            throw new KeyNotFoundException($"No master raster for {px}x{px}.");
        return new Bitmap(bitmap); // clone so caller can dispose freely
    }

    public static MasterRasters Load(string repoRoot)
    {
        var iconsDir = Path.Combine(repoRoot, "images", "icons");
        if (!Directory.Exists(iconsDir))
            throw new DirectoryNotFoundException($"{iconsDir} not found");

        var byPx = new Dictionary<int, Bitmap>();

        int[] sizes = { 16, 32, 64, 128, 256, 512, 1024 };
        foreach (var px in sizes)
        {
            var path = Path.Combine(iconsDir, $"icon_{px}.png");
            if (!File.Exists(path)) continue;
            byPx[px] = new Bitmap(path);
        }

        if (byPx.Count == 0)
            throw new InvalidOperationException($"No master rasters found in {iconsDir}");

        return new MasterRasters(byPx);
    }

    public void Dispose()
    {
        foreach (var b in _byPx.Values) b.Dispose();
        _byPx.Clear();
    }
}
