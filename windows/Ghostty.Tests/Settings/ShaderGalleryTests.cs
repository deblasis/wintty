using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Collection name for the gallery's static seams; see the attribute below.
/// </summary>
public static class ShaderGalleryCollection
{
    public const string Name = "ShaderGallerySeams";
}

// The gallery's seams (ShaderGallery.TestEntries / TestBaseDirectory) are
// process-wide statics, so every test that touches one shares this collection:
// xunit runs test CLASSES in parallel by default, and a token test whose
// catalog is nulled by another class's finally reads as "the token resolves
// to nothing".
[Collection(ShaderGalleryCollection.Name)]
public class ShaderGalleryTests
{
    private static string RepoBase()
    {
        // Test bin lives at windows/Ghostty.Tests/bin/<cfg>/<tfm>; the repo
        // root is five levels up from there.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "windows", "Ghostty", "Assets", "Shaders")))
                return Path.Combine(dir.FullName, "windows", "Ghostty");
            dir = dir.Parent;
        }
        return string.Empty;
    }

    [Fact]
    public void ManifestDeserializesAndKeepsAllEntries()
    {
        var repo = RepoBase();
        if (repo.Length == 0)
        {
            // Not running from a checkout layout (e.g. CI artifact); nothing
            // to assert against.
            return;
        }

        ShaderGallery.TestBaseDirectory = repo;
        ShaderGallery.ManifestParser = json =>
            System.Text.Json.JsonSerializer.Deserialize<TestManifest>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Shaders;
        try
        {
            var entries = ShaderGallery.Entries;
            Assert.True(entries.Count >= 11,
                $"expected at least 11 bundled shaders, got {entries.Count}; " +
                $"load detail: {ShaderGallery.LoadDetail ?? "(none)"}");
            Assert.All(entries, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Name));
                Assert.False(string.IsNullOrWhiteSpace(e.File));
                Assert.True(File.Exists(ShaderGallery.AbsolutePathFor(e)),
                    $"gallery file missing: {e.File}");
            });
        }
        finally
        {
            ShaderGallery.TestBaseDirectory = null;
        }
    }
}

/// <summary>
/// What a gallery pick persists. The pick used to commit
/// <c>&lt;install dir&gt;\Assets\Shaders\xxx.glsl</c> straight into the user's
/// config, which is a promise about a directory the next update, move, or
/// install prefix can invalidate -- and nothing noticed when it did: the
/// shader silently stopped applying with the config still naming a file that
/// is no longer there. The token keeps the reference and is resolved where the
/// shader is used, the way <c>brand:</c>/<c>bundled:</c> already are for icons.
/// </summary>
[Collection(ShaderGalleryCollection.Name)]
public sealed class ShaderGalleryTokenTests
{
    private static readonly ShaderGalleryEntry Crt = new(
        "crt", "crt.frag", "CRT", "Curvature and scanlines", "Effects",
        "someone", "MIT", "https://example.invalid/crt");

    private static readonly ShaderGalleryEntry Bloom = new(
        "bloom", "bloom.frag", "Bloom", "Soft glow", "Effects",
        "someone", "MIT", "https://example.invalid/bloom");

    private static IReadOnlyList<ShaderGalleryEntry> Installed(params ShaderGalleryEntry[] entries)
    {
        ShaderGallery.TestEntries = entries;
        return entries;
    }

    [Fact]
    public void A_pick_persists_the_id_and_not_the_installed_path()
    {
        Installed(Crt, Bloom);
        try
        {
            var token = ShaderGallery.TokenFor(Crt);

            Assert.Equal("gallery:crt", token);
            // The whole defect in one assertion: the token must not carry a
            // path, because a path is what the next install prefix invalidates.
            Assert.DoesNotContain(
                Path.DirectorySeparatorChar.ToString(),
                token,
                StringComparison.Ordinal);
            Assert.True(ShaderGallery.IsToken(token));
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    /// <summary>
    /// The tail every resolved gallery path ends in, asserted rather than a
    /// whole-path comparison. <c>AbsolutePathFor</c> reads the process-wide
    /// TestBaseDirectory, which another test class in this assembly sets and
    /// clears around its own loader test; a whole-path equality between two
    /// reads is then a coin flip, while the tail is the property this is
    /// actually about.
    /// </summary>
    private static string ExpectedTail(ShaderGalleryEntry entry)
        => Path.Combine("Assets", "Shaders", entry.File);

    [Fact]
    public void A_token_resolves_to_this_installs_path()
    {
        Installed(Crt, Bloom);
        try
        {
            var resolved = ShaderGallery.ResolveTokenToPath("gallery:crt");

            Assert.NotNull(resolved);
            Assert.EndsWith(ExpectedTail(Crt), resolved!, StringComparison.OrdinalIgnoreCase);
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void A_token_names_the_same_entry_whatever_its_casing()
    {
        Installed(Crt);
        try
        {
            Assert.Equal(Crt, ShaderGallery.FindById("CRT"));
            Assert.EndsWith(
                ExpectedTail(Crt),
                ShaderGallery.ResolveTokenToPath("GALLERY:crt")!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void A_token_this_install_does_not_ship_resolves_to_nothing()
    {
        // The case a rewrite would get wrong: an entry dropped from the
        // manifest between versions, or a config carried from a build that
        // shipped it. Resolving it to some path would swap one dead reference
        // for another, so it stays unresolved and the caller keeps the text.
        Installed(Bloom);
        try
        {
            // The token is still well formed -- only its id is unknown -- so
            // the page can say which entry is missing instead of calling it a
            // file path.
            Assert.Null(ShaderGallery.ResolveTokenToPath("gallery:crt"));
            Assert.True(ShaderGallery.TryGetTokenId("gallery:crt", out var id));
            Assert.Equal("crt", id);
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:/shaders/mine.glsl")]
    [InlineData("brand:pwsh")]
    public void Only_our_prefix_is_our_token(string? value)
    {
        Assert.False(ShaderGallery.IsToken(value));
        Assert.False(ShaderGallery.TryGetTokenId(value, out var id));
        Assert.Equal(string.Empty, id);
    }

    /// <summary>
    /// A bare prefix is ours but names nothing, so it is not a token the rest of
    /// the app may act on. The two questions are separate on purpose (see
    /// <c>IsToken</c>), and this is where the second one answers.
    /// </summary>
    [Theory]
    [InlineData("gallery:")]
    [InlineData("gallery:   ")]
    [InlineData("GALLERY:")]
    public void A_prefix_with_no_id_is_not_a_token_to_act_on(string value)
    {
        Assert.True(ShaderGallery.IsToken(value));
        Assert.False(ShaderGallery.TryGetTokenId(value, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void A_path_is_not_something_the_resolver_claims()
    {
        Installed(Crt);
        try
        {
            // Null, not the path: the caller asked whether this value is one of
            // ours to resolve, and a hand-written path is not.
            Assert.Null(ShaderGallery.ResolveTokenToPath("C:/shaders/mine.glsl"));
        }
        finally { ShaderGallery.TestEntries = null; }
    }
}

[Collection(ShaderGalleryCollection.Name)]
public sealed class ShaderGalleryOverlayTests
{
    private static readonly ShaderGalleryEntry Crt = new(
        "crt", "crt.frag", "CRT", "Curvature and scanlines", "Effects",
        "someone", "MIT", "https://example.invalid/crt");

    private static void Install() => ShaderGallery.TestEntries = new[] { Crt };

    [Fact]
    public void A_config_with_no_token_layers_nothing()
    {
        Install();
        try
        {
            Assert.Null(ShaderGalleryOverlay.RenderFor(new[] { "C:/shaders/mine.glsl" }));
            Assert.Null(ShaderGalleryOverlay.RenderFor(Array.Empty<string>()));
            Assert.Null(ShaderGalleryOverlay.RenderFor(null!));
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void The_token_is_replaced_by_this_installs_path()
    {
        Install();
        try
        {
            var body = ShaderGalleryOverlay.RenderFor(new[] { "gallery:crt" });

            Assert.NotNull(body);
            Assert.Contains("crt.frag", body!, System.StringComparison.Ordinal);
            Assert.DoesNotContain("gallery:crt", body!, System.StringComparison.Ordinal);
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void The_list_is_cleared_before_it_is_restated()
    {
        // custom-shader is a RepeatablePath, and a non-empty assignment
        // APPENDS to it (ghostty's parser). An overlay that only added its
        // lines would leave the token ahead of the resolved path in the list,
        // and the renderer aborts the whole chain on the first entry it cannot
        // open -- so the overlay would reproduce the no-shader it exists to
        // fix. The empty value is what resets the list.
        Install();
        try
        {
            var body = ShaderGalleryOverlay.RenderFor(new[] { "gallery:crt" })!;

            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("custom-shader", System.StringComparison.Ordinal))
                .ToList();

            Assert.Equal("custom-shader =", lines[0]);
            Assert.Equal(2, lines.Count);
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void A_hand_written_path_alongside_a_token_is_kept()
    {
        // Resetting the key and restating only the resolved token would drop
        // an entry the user has always had.
        Install();
        try
        {
            var body = ShaderGalleryOverlay.RenderFor(
                new[] { "gallery:crt", "C:/shaders/mine.glsl" })!;

            Assert.Contains("crt.frag", body, System.StringComparison.Ordinal);
            Assert.Contains("C:/shaders/mine.glsl", body, System.StringComparison.Ordinal);
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void A_token_this_install_lacks_is_passed_through()
    {
        // Nothing to rewrite it to, and libghostty already has a notice for a
        // shader file it cannot open. Rewriting would only pick a different
        // wrong answer.
        Install();
        try
        {
            var body = ShaderGalleryOverlay.RenderFor(new[] { "gallery:gone", "gallery:crt" })!;

            Assert.Contains("gallery:gone", body, System.StringComparison.Ordinal);
            Assert.Contains("crt.frag", body, System.StringComparison.Ordinal);
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void An_unresolvable_token_alone_layers_nothing()
    {
        // There is nothing to layer: the user's own line already says what
        // libghostty reads, and restating it verbatim would only add a file
        // that changes nothing. The token's failure to open is reported by the
        // app-level shader notice the app already has.
        Install();
        try
        {
            Assert.Null(ShaderGalleryOverlay.RenderFor(new[] { "gallery:gone" }));
        }
        finally { ShaderGallery.TestEntries = null; }
    }

    [Fact]
    public void Every_entry_is_a_line_libghostty_can_parse()
    {
        Install();
        try
        {
            var body = ShaderGalleryOverlay.RenderFor(new[] { "gallery:crt" })!;

            Assert.All(
                body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()),
                line => Assert.True(
                    line.StartsWith("#", StringComparison.Ordinal) || line.Contains('='),
                    $"not a comment and not an assignment: {line}"));
        }
        finally { ShaderGallery.TestEntries = null; }
    }
}

public sealed class TestManifest
{
    [System.Text.Json.Serialization.JsonPropertyName("shaders")]
    public List<ShaderGalleryEntry>? Shaders { get; set; }
}
