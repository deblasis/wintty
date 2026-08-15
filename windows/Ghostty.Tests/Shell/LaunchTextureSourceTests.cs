using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="LaunchTextureSource"/>. The contract is an
/// order: a texture the user supplied must win over the one that shipped,
/// and the shipped one must still be found when they have not supplied any.
/// Getting it backwards would silently ignore a customisation, which looks
/// like the feature simply not working.
/// </summary>
public sealed class LaunchTextureSourceTests
{
    private const string AppData = @"C:\Users\someone\AppData\Roaming";
    private const string BaseDir = @"C:\Program Files\Wintty";

    private static readonly string Wintty =
        Path.Combine(AppData, "wintty", LaunchTextureSource.UserFileName);

    private static readonly string Ghostty =
        Path.Combine(AppData, "ghostty", LaunchTextureSource.UserFileName);

    private static readonly string Shipped =
        Path.Combine(BaseDir, "Assets", LaunchTextureSource.ShippedFileName);

    private static Func<string, bool> Only(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void Users_texture_wins_over_the_shipped_one()
    {
        var found = LaunchTextureSource.Resolve(AppData, BaseDir, Only(Wintty, Shipped));

        Assert.NotNull(found);
        Assert.Equal(Wintty, found!.Value.Path);
        Assert.True(found.Value.IsUserSupplied);
    }

    [Fact]
    public void Shipped_texture_is_used_when_the_user_has_supplied_none()
    {
        var found = LaunchTextureSource.Resolve(AppData, BaseDir, Only(Shipped));

        Assert.NotNull(found);
        Assert.Equal(Shipped, found!.Value.Path);
        Assert.False(found.Value.IsUserSupplied);
    }

    [Fact]
    public void The_current_application_name_is_preferred_over_the_older_one()
    {
        // Both directory names are read for the same reason the theme search
        // reads both, so which one wins has to be settled rather than left
        // to whichever the enumerator happens to reach first.
        var found = LaunchTextureSource.Resolve(AppData, BaseDir, Only(Wintty, Ghostty, Shipped));

        Assert.Equal(Wintty, found!.Value.Path);
    }

    [Fact]
    public void The_older_application_name_is_still_read()
    {
        var found = LaunchTextureSource.Resolve(AppData, BaseDir, Only(Ghostty, Shipped));

        Assert.Equal(Ghostty, found!.Value.Path);
        Assert.True(found.Value.IsUserSupplied);
    }

    [Fact]
    public void Nothing_anywhere_is_not_a_fault()
    {
        // A build carrying no sheet draws a plain splash. It must come back
        // empty rather than throw, because this runs before the app is on
        // screen and there is nothing there to report a failure to.
        Assert.Null(LaunchTextureSource.Resolve(AppData, BaseDir, Only()));
    }

    [Theory]
    [InlineData(null, BaseDir)]
    [InlineData(AppData, null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Missing_roots_are_skipped_rather_than_combined(string? appData, string? baseDir)
    {
        // Path.Combine on a null throws and on an empty string quietly
        // yields a relative path, which would then be probed against the
        // working directory -- somewhere neither the user nor the installer
        // ever puts anything.
        var candidates = LaunchTextureSource.Candidates(appData, baseDir).ToList();

        Assert.All(candidates, c => Assert.True(Path.IsPathFullyQualified(c.Path), c.Path));
        Assert.Null(LaunchTextureSource.Resolve(appData, baseDir, Only()));
    }

    [Fact]
    public void The_user_file_is_named_for_where_it_lives()
    {
        // It sits beside the config and theme files, which are lower case,
        // rather than beside the shipped asset.
        Assert.Equal(LaunchTextureSource.UserFileName.ToLowerInvariant(),
            LaunchTextureSource.UserFileName);
        Assert.EndsWith(".png", LaunchTextureSource.UserFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_probe_is_rejected_rather_than_dereferenced()
    {
        Assert.Throws<ArgumentNullException>(
            () => LaunchTextureSource.Resolve(AppData, BaseDir, null!));
    }
}
