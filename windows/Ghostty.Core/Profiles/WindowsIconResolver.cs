using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Production IIconResolver. Handles all five IconSpec variants.
/// Results are SHA-keyed and cached to
/// %LOCALAPPDATA%\Wintty\IconCache\&lt;sha&gt;.png; subsequent resolves
/// read from disk rather than recomputing. Unknown bundled keys fall
/// back to the "default" bundled asset.
/// </summary>
internal sealed class WindowsIconResolver(IFileSystem fs) : IIconResolver
{
    private const string DefaultBundledKey = "default";

    public async Task<byte[]> ResolveAsync(IconSpec spec, CancellationToken ct)
    {
        var cacheKey = SpecToCacheKey(spec);
        var cached = await TryReadCacheAsync(cacheKey, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var bytes = await ResolveUncachedAsync(spec, ct).ConfigureAwait(false);
        await TryWriteCacheAsync(cacheKey, bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    // Mtime-sensitive icon sources: upgrading the exe or swapping the
    // user's custom .ico must invalidate the cached PNG. Bundled assets,
    // MDL2 codepoints, and the WSL fallback are stable for the life of
    // the build; no mtime needed.
    private string? MtimeTokenFor(IconSpec spec)
    {
        var path = spec switch
        {
            IconSpec.Path p => p.FilePath,
            IconSpec.AutoForExe a => a.ExePath,
            _ => null,
        };
        if (path is null) return null;
        var utc = fs.GetLastWriteTimeUtc(path);
        return utc is null ? null : utc.Value.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<byte[]> ResolveUncachedAsync(IconSpec spec, CancellationToken ct) => spec switch
    {
        IconSpec.Path p => await fs.ReadAllBytesAsync(p.FilePath, ct).ConfigureAwait(false),
        IconSpec.BundledKey b => ReadBundledOrDefault(b.Key),
        IconSpec.Mdl2Token => ReadBundledOrDefault(DefaultBundledKey),
        IconSpec.AutoForExe a => ExtractExeIconAsPng(a.ExePath),
        IconSpec.AutoForWslDistro => ReadBundledOrDefault("wsl"),
        _ => ReadBundledOrDefault(DefaultBundledKey),
    };

    // Best-effort: any SHGetFileInfoW / GDI failure falls back to the
    // default bundled icon rather than failing a profile resolve.
    private static byte[] ExtractExeIconAsPng(string exePath)
    {
        // IsWindowsVersionAtLeast (not bare IsWindows) satisfies CA1416's
        // narrowing for Win32IconExtractor's [SupportedOSPlatform("windows6.0.6000")].
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
            return ReadBundledOrDefault(DefaultBundledKey);
        try
        {
            return Win32IconExtractor.ExtractAsPng16(exePath);
        }
        // Mirrors the Try*CacheAsync pattern: cancellation surfaces, everything
        // else (GDI failures, missing .exe, access-denied) silently falls back.
        catch (OperationCanceledException) { throw; }
        catch
        {
            return ReadBundledOrDefault(DefaultBundledKey);
        }
    }

    private static byte[] ReadBundledOrDefault(string key)
    {
        var bytes = TryReadBundled(key);
        return bytes ?? TryReadBundled(DefaultBundledKey)
            ?? throw new InvalidOperationException("default bundled icon is missing");
    }

    private static byte[]? TryReadBundled(string key)
    {
        var resource = $"Ghostty.Core.Profiles.IconAssets.{key}.png";
        using var stream = typeof(WindowsIconResolver).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private string SpecToCacheKey(IconSpec spec)
    {
        // Casing note: Windows paths are case-insensitive, so tokens
        // like "exe:C:\\Foo.exe" and "exe:c:\\foo.exe" currently hash
        // to different SHAs and produce duplicate cache entries for
        // the same underlying file. Acceptable for Path (user-supplied,
        // round-trips verbatim).
        var baseToken = spec switch
        {
            IconSpec.Path p => "path:" + p.FilePath,
            IconSpec.Mdl2Token m => "mdl2:" + m.CodePoint.ToString("x"),
            IconSpec.BundledKey b => "bundled:" + b.Key,
            IconSpec.AutoForExe a => "exe:" + a.ExePath,
            IconSpec.AutoForWslDistro w => "wsl-distro:" + w.DistroName,
            _ => "unknown",
        };
        var mtime = MtimeTokenFor(spec);
        var token = mtime is null ? baseToken : baseToken + "|mtime:" + mtime;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<byte[]?> TryReadCacheAsync(string sha, CancellationToken ct)
    {
        var path = CachePathFor(sha);
        if (path is null || !fs.FileExists(path)) return null;
        try { return await fs.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
        // Cancellation must surface so the caller sees the token was honored;
        // a blanket catch would silently fall through to uncached resolution.
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task TryWriteCacheAsync(string sha, byte[] bytes, CancellationToken ct)
    {
        var path = CachePathFor(sha);
        if (path is null) return;
        try { await fs.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
    }

    private string? CachePathFor(string sha)
    {
        var local = fs.GetKnownFolder(KnownFolderId.LocalAppData);
        return local is null ? null : Path.Combine(local, "Wintty", "IconCache", sha + ".png");
    }
}
