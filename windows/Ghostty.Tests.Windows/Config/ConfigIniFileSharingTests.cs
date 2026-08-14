using System;
using System.IO;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Windows.Config;

/// <summary>
/// The rest of the parser's behaviour is pure logic and lives in the
/// cross-platform test project. This one asserts a sharing rule, which is
/// Windows-native: elsewhere .NET emulates FileShare with advisory locks and
/// the guarantee is not the same.
/// </summary>
public sealed class ConfigIniFileSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"wintty-ini-share-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FileLockedAgainstReaders_Throws()
    {
        // The doc promises propagation rather than an empty dictionary, and the
        // config service relies on it: a half-read config is worse than none,
        // so it lets the throw escape rather than starting on defaults.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.wintty");
        File.WriteAllText(path, "windows-single-instance = true\n");

        using var exclusive = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => ConfigIniFile.Load(path));
    }
}
