using System.Collections.Concurrent;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;

namespace Ghostty.Tabs;

/// <summary>
/// Process-wide cache mapping resolved <see cref="IconSpec"/> values to
/// their PNG bytes. <see cref="GetBytesSync"/> blocks the UI thread for
/// the first miss per spec; subsequent hits are O(1). Bundled keys and
/// PNG paths resolve in well under a millisecond; SVG rasterization is
/// one-shot per cache miss. The bytes-converter consumed by the tab
/// strip's IValueConverter has no async hook, which forces the sync
/// shape here.
/// </summary>
internal static class TabIconBytesCache
{
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private static IIconResolver? _resolver;

    public static void Install(IIconResolver resolver)
    {
        _resolver = resolver;
    }

    public static byte[]? GetBytesSync(IconSpec spec)
    {
        if (_resolver is null) return null;
        var key = SpecKey(spec);
        return _cache.GetOrAdd(key, _ =>
            Task.Run(() => _resolver.ResolveAsync(spec, default)).GetAwaiter().GetResult());
    }

    private static string SpecKey(IconSpec spec) => spec switch
    {
        IconSpec.BrandKey b => $"brand-{b.Key}-{b.Dpi ?? 0}",
        IconSpec.BundledKey b => $"bundled-{b.Key}",
        IconSpec.Path p => $"path-{p.FilePath}",
        IconSpec.AutoForExe a => $"exe-{a.ExePath}",
        IconSpec.AutoForWslDistro w => $"wsl-{w.DistroName}",
        IconSpec.Mdl2Token m => $"mdl2-{m.CodePoint:x}",
        _ => "default",
    };
}
