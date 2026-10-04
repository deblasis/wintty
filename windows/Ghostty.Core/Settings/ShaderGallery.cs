using System;
using System.Collections.Generic;
using System.IO;


namespace Ghostty.Core.Settings;

/// <summary>
/// One entry of the bundled shader gallery, deserialized from
/// Assets/Shaders/shaders.json. The manifest is the single source of truth:
/// the verify pipeline (tools/gallery/verify.sh in the repo) compiles and
/// renders every entry it lists, and this catalog shows the same entries
/// in the settings UI.
/// </summary>
public sealed record ShaderGalleryEntry(
    string Id,
    string File,
    string Name,
    string Description,
    string Category,
    string Author,
    string License,
    string Source);

/// <summary>
/// Loads the bundled shader gallery from the app's installed assets. Lives in
/// Core so the test project can drive it against the source tree (a test bin
/// does not receive the app's Content items). The directory resolves the same
/// way in every deployment mode: AppContext.BaseDirectory is the package/exe
/// directory and Content items land in Assets\Shaders next to it.
/// </summary>
public static class ShaderGallery
{
    private static readonly Lazy<IReadOnlyList<ShaderGalleryEntry>> _entries = new(Load);

    /// <summary>
    /// The persisted form of a gallery pick: the manifest entry's id, not the
    /// path its file happens to sit at today.
    /// </summary>
    /// <remarks>
    /// The installed path is an install-time fact. Writing it into the user's
    /// config made every pick a promise about a directory the next update (or
    /// a move, or a different install prefix) can invalidate, and nothing
    /// noticed: the shader simply stopped applying, with the config still
    /// naming a file that is not there any more. A token keeps the reference
    /// and resolves it where the shader is used, which is what the icon tokens
    /// (<c>brand:</c>, <c>bundled:</c>) have always done for icons -- see
    /// <c>ProfileSourceParser.ParseIcon</c> and the resolver behind it.
    /// libghostty only knows paths, so the value is resolved before the config
    /// reaches it; see <see cref="ShaderGalleryOverlay"/>.
    /// </remarks>
    public const string TokenPrefix = "gallery:";

    public static IReadOnlyList<ShaderGalleryEntry> Entries
        => TestEntries ?? _entries.Value;

    /// <summary>
    /// Human-readable detail about how the last load went (why the gallery is
    /// empty, when it is). Set by Load; callers surface it through their own
    /// logger. Null after a successful non-empty load.
    /// </summary>
    public static string? LoadDetail { get; private set; }

    /// <summary>
    /// Tests inject a checkout-relative base here so the loader runs against
    /// the source Assets/Shaders. Null in production: AppContext.BaseDirectory
    /// is used. Must be set before the first Entries read.
    /// </summary>
    public static string? TestBaseDirectory { get; set; }

    /// <summary>
    /// Tests stand the whole catalog in here, for the token helpers: resolving
    /// a token needs an entry to resolve to, and reading <see cref="Entries"/>
    /// would both depend on the loader's Lazy singleton (which another test
    /// class may already have populated from a different base directory) and
    /// need real shader files on disk. Null in production.
    /// </summary>
    public static IReadOnlyList<ShaderGalleryEntry>? TestEntries { get; set; }

    /// <summary>
    /// Parses the shaders.json text into entries. Set by the app (source-
    /// generated, NativeAOT-safe) and by tests (reflection, JIT). Must be
    /// set before the first Entries read.
    /// </summary>
    public static Func<string, List<ShaderGalleryEntry>?>? ManifestParser { get; set; }



    private static IReadOnlyList<ShaderGalleryEntry> Load()
    {
        try
        {
            var baseDir = TestBaseDirectory ?? AppContext.BaseDirectory;
            var dir = Path.Combine(baseDir, "Assets", "Shaders");
            var manifestPath = Path.Combine(dir, "shaders.json");
            if (!File.Exists(manifestPath))
            {
                LoadDetail = $"manifest not found at {manifestPath}";
                return Array.Empty<ShaderGalleryEntry>();
            }

            var json = File.ReadAllText(manifestPath);

            // The app publishes NativeAOT (reflection serialization
            // disabled), and the STJ source generator does not fire in this
            // project -- so the manifest binding is INJECTED by the app
            // (ShaderGalleryJson, source-generated context) and by tests
            // (reflection is fine under JIT). A missing parser is a wiring
            // bug and surfaces through LoadDetail.
            var parser = ManifestParser;
            if (parser is null)
            {
                LoadDetail = "no manifest parser wired";
                return Array.Empty<ShaderGalleryEntry>();
            }
            var entries = parser(json) ?? new List<ShaderGalleryEntry>();
            var parsedCount = entries.Count;
            // Keep only entries whose file actually shipped; a stale manifest
            // entry must not offer a shader the renderer cannot load.
            entries.RemoveAll(e =>
                string.IsNullOrWhiteSpace(e.File) ||
                !File.Exists(Path.Combine(dir, e.File)));
            if (entries.Count == 0)
            {
                LoadDetail = $"manifest at {manifestPath} yielded no entries " +
                             $"(parsed {parsedCount}, kept 0 -- files missing at {dir})";
            }
            else
            {
                LoadDetail = null;
            }
            return entries.AsReadOnly();
        }
        catch (Exception ex)
        {
            // A missing or malformed manifest degrades to "no bundled shaders"
            // (the custom-path UI still works), never to a settings crash.
            LoadDetail = $"load failed: {ex.GetType().Name}: {ex.Message}";
            return Array.Empty<ShaderGalleryEntry>();
        }
    }

    /// <summary>
    /// Absolute installed path of a gallery shader file. This is a live fact
    /// about this install, never something to write into the user's config:
    /// see <see cref="TokenPrefix"/> for what is written instead.
    /// </summary>
    public static string AbsolutePathFor(ShaderGalleryEntry entry) =>
        Path.Combine(TestBaseDirectory ?? AppContext.BaseDirectory, "Assets", "Shaders", entry.File);

    /// <summary>
    /// The value to write to <c>custom-shader</c> for this entry.
    /// </summary>
    public static string TokenFor(ShaderGalleryEntry entry) => TokenPrefix + entry.Id;

    /// <summary>
    /// True when <paramref name="value"/> begins with our prefix, whatever
    /// follows it. Case-insensitive, like the config keys themselves.
    /// </summary>
    /// <remarks>
    /// Says nothing about whether the id names an entry: that is
    /// <see cref="TryGetTokenId"/>'s answer, and a bare <c>gallery:</c> is
    /// this returning true with an empty id. Split deliberately -- the page's
    /// "is this one of ours" branch and the parse of what it names are two
    /// different questions, and the second one reports malformed input
    /// instead of mislabelling it as a path.
    /// </remarks>
    public static bool IsToken(string? value)
        => value is not null
            && value.StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The manifest id a <c>gallery:</c> token names, or false for anything
    /// else -- including <c>gallery:</c> on its own, which names nothing.
    /// </summary>
    public static bool TryGetTokenId(string? value, out string id)
    {
        id = string.Empty;
        if (!IsToken(value)) return false;

        id = value![TokenPrefix.Length..].Trim();
        return id.Length > 0;
    }

    /// <summary>
    /// The entry a token names, or null when the id is not in this install's
    /// manifest -- an entry dropped between versions, or a config carried from
    /// a build that shipped it. Null is the answer callers must handle: it is
    /// the difference between "resolve at use time" and "rewrite to a path
    /// that is already dead".
    /// </summary>
    public static ShaderGalleryEntry? FindById(string id)
        => Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The absolute path a configured <c>custom-shader</c> value stands for, or
    /// null when it is not one of ours to resolve.
    /// </summary>
    /// <remarks>
    /// A value that is not a token is a path already, and a token whose entry
    /// is gone resolves to nothing rather than to a path: the caller keeps the
    /// user's own text (so the settings UI can say the entry is not installed
    /// and libghostty can report the load failure it already has a notice
    /// for) instead of being handed a path that cannot be opened.
    /// </remarks>
    public static string? ResolveTokenToPath(string? value)
        => TryGetTokenId(value, out var id) && FindById(id) is { } entry
            ? AbsolutePathFor(entry)
            : null;
}
