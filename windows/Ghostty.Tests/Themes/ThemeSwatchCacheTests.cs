using System;
using System.Collections.Generic;
using System.IO;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// A long theme list stays responsive because a row's file is read once per
/// browse and only for rows the list realizes; these pin the "once", the
/// "never for a name the config cannot carry", and the cap on a stray file.
/// </summary>
public class ThemeSwatchCacheTests
{
    private sealed class Reader
    {
        public Dictionary<string, string[]> Files { get; } = new();
        public List<string> Reads { get; } = new();

        public IEnumerable<string>? Read(string name)
        {
            Reads.Add(name);
            return Files.TryGetValue(name, out var lines) ? lines : null;
        }
    }

    [Fact]
    public void EachThemeIsReadOnce()
    {
        var reader = new Reader();
        reader.Files["Nord"] = ["background = #2E3440"];
        var cache = new ThemeSwatchCache(reader.Read);

        Assert.False(cache.TryGetCached("Nord", out _));
        Assert.Equal(0x2E3440u, cache.Get("Nord")!.Background);
        Assert.Equal(0x2E3440u, cache.Get("Nord")!.Background);
        Assert.True(cache.TryGetCached("Nord", out var cached));
        Assert.Equal(0x2E3440u, cached!.Background);
        Assert.Equal(new[] { "Nord" }, reader.Reads);
    }

    [Fact]
    public void AMissingThemeIsRememberedAsNull()
    {
        var reader = new Reader();
        var cache = new ThemeSwatchCache(reader.Read);

        Assert.Null(cache.Get("Gone"));
        Assert.Null(cache.Get("Gone"));
        Assert.True(cache.TryGetCached("Gone", out var cached));
        Assert.Null(cached);
        Assert.Single(reader.Reads);
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("a/b")]
    [InlineData("light:A,dark:B")]
    [InlineData(" padded")]
    [InlineData("")]
    public void ANameTheConfigCannotCarryIsNeverRead(string name)
    {
        var reader = new Reader();
        var cache = new ThemeSwatchCache(reader.Read);

        Assert.Null(cache.Get(name));
        Assert.Empty(reader.Reads);
    }

    [Fact]
    public void AnUnreadableFileIsABlankSwatchNotACrash()
    {
        // File.ReadLines is lazy, so the failure surfaces while parsing.
        static IEnumerable<string> Throws()
        {
            yield return "background = #101010";
            throw new IOException("sharing violation");
        }

        var cache = new ThemeSwatchCache(_ => Throws());
        Assert.Null(cache.Get("Locked"));
        Assert.True(cache.TryGetCached("Locked", out var cached));
        Assert.Null(cached);
    }

    [Fact]
    public void AStrayLargeFileIsReadOnlyUpToTheCap()
    {
        var yielded = 0;
        IEnumerable<string> Endless()
        {
            while (true)
            {
                if (++yielded > ThemeSwatchCache.MaxLines + 10)
                    throw new InvalidOperationException("read past the cap");
                yield return "palette = 0=#000001";
            }
        }

        var cache = new ThemeSwatchCache(_ => Endless());
        Assert.Equal(0x000001u, cache.Get("Huge")!.Palette[0]);
        Assert.Equal(ThemeSwatchCache.MaxLines, yielded);
    }

    [Fact]
    public void ClearMakesTheNextBrowseReadAgain()
    {
        var reader = new Reader();
        reader.Files["Nord"] = ["background = #2E3440"];
        var cache = new ThemeSwatchCache(reader.Read);
        cache.Get("Nord");

        reader.Files["Nord"] = ["background = #3B4252"];
        cache.Clear();

        Assert.False(cache.TryGetCached("Nord", out _));
        Assert.Equal(0x3B4252u, cache.Get("Nord")!.Background);
        Assert.Equal(2, reader.Reads.Count);
    }

    [Fact]
    public void TheFirstDirectoryWithTheThemeIsTheOneRead()
    {
        // The same shadowing libghostty applies when it loads `theme = T`.
        var root = Path.Combine(Path.GetTempPath(), "wintty-swatch-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, "Shadowed"), "background = #111111\n");
            File.WriteAllText(Path.Combine(second, "Shadowed"), "background = #222222\n");
            File.WriteAllText(Path.Combine(second, "Later"), "background = #333333\n");
            var cache = ThemeSwatchCache.ForDirectories(() => ["", first, second]);

            Assert.Equal(0x111111u, cache.Get("Shadowed")!.Background);
            Assert.Equal(0x333333u, cache.Get("Later")!.Background);
            Assert.Null(cache.Get("Nowhere"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
