using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Resolves an <see cref="IconSpec"/> to 16x16 PNG bytes. Production
/// wrapper handles SHGetFileInfo extraction, MDL2 glyph rasterization,
/// bundled-icon resource loading, and on-disk caching at
/// %LOCALAPPDATA%\Wintty\IconCache\&lt;sha&gt;.png. Tests use
/// FakeIconResolver.
/// </summary>
public interface IIconResolver
{
    Task<byte[]> ResolveAsync(IconSpec spec, CancellationToken ct);
}
