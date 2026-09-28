using System;
using System.IO;
using Ghostty.Core.Config;
using Ghostty.Core.Motion;
using Ghostty.Tests.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The <c>animations</c> key survives a settings round-trip: each of the
/// three values is written through the editor contract the settings page
/// uses, read back from a fresh editor over the same file (what the next
/// page construction, and the next process, sees), and parses back to the
/// rung it was written for.
/// </summary>
public class AnimationsSettingTests
{
    private static string TempPath() => Path.Combine(
        Path.GetTempPath(),
        "wintty-anim-tests-" + Guid.NewGuid().ToString("N") + ".conf");

    /// <summary>Write, then read back through a NEW editor over the same
    /// file, so the read is from disk rather than from any in-memory
    /// state the writing editor holds.</summary>
    private static string? RoundTrip(string written)
    {
        var path = TempPath();
        try
        {
            new TempFileEditor(path).SetValue("animations", written);
            return new TempFileEditor(path).GetRepeatableValues("animations") is
                { Length: 1 } values
                ? values[0]
                : null;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("system", UserMotionLever.FollowSystem)]
    [InlineData("reduced", UserMotionLever.Reduced)]
    [InlineData("off", UserMotionLever.Off)]
    public void Each_written_value_survives_a_reload_and_parses_back(
        string written, UserMotionLever expected)
    {
        var readBack = RoundTrip(written);
        Assert.Equal(written, readBack);
        Assert.Equal(expected, UserMotionLeverValues.Parse(readBack));
    }

    [Fact]
    public void An_absent_key_reads_as_follow_system()
    {
        // No line: the file holds nothing for the key, and the vocabulary
        // helper's fallback is the card's default.
        var path = TempPath();
        try
        {
            new TempFileEditor(path).WriteRaw("# wintty config\n");
            var values = new TempFileEditor(path).GetRepeatableValues("animations");
            Assert.Empty(values);
            Assert.Equal(UserMotionLever.FollowSystem, UserMotionLeverValues.Parse(null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rewriting_the_key_replaces_rather_than_appends()
    {
        // The combo can move between rungs within one session; the second
        // write must leave ONE line behind, not two.
        var path = TempPath();
        try
        {
            var editor = new TempFileEditor(path);
            editor.SetValue("animations", "system");
            editor.SetValue("animations", "off");
            var values = new TempFileEditor(path).GetRepeatableValues("animations");
            Assert.Single(values);
            Assert.Equal("off", values[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
