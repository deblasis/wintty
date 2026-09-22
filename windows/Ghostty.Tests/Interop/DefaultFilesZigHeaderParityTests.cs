using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Interop;

/// <summary>
/// The third side of <c>ghostty_config_default_files_e</c>: the zig
/// declaration it is generated from.
/// </summary>
/// <remarks>
/// <para>Two parity checks already existed and they were not enough between
/// them. <c>GhosttyActionTagHeaderParityTests</c> checks the managed
/// <c>ConfigFilesFound</c> against include/ghostty.h, and a zig test checks
/// <c>Config.DefaultFiles</c> against literals of its own. Move the header
/// and the managed enum together and both stay green while zig disagrees
/// with the ABI, which is a reload that reads "the user's config loaded" as
/// "no config file exists" and applies pure defaults over it.</para>
///
/// <para>It lives here rather than in zig because <c>@embedFile</c> cannot
/// reach include/ghostty.h from src/: the header is outside the source
/// package. This assembly already embeds both files.</para>
/// </remarks>
public class DefaultFilesZigHeaderParityTests
{
    private const string HeaderResource = "Ghostty.Tests.Interop.Header.ghostty.h";
    private const string ZigResource = "Ghostty.Tests.Config.Defaults.Config.zig";

    private const string Typedef = "ghostty_config_default_files_e";
    private const string Prefix = "GHOSTTY_CONFIG_DEFAULT_FILES_";
    private const string ZigDecl = "pub const DefaultFiles = enum(c_int) {";

    [Fact]
    public void The_zig_enum_carries_the_values_the_header_publishes()
    {
        var header = ReadHeaderEnum();
        var zig = ReadZigEnum();

        Assert.Equal(
            header.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
            zig.OrderBy(p => p.Key, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// And the managed mirror is the same set again, by name and value. The
    /// header check is per member, so a member present in two of the three
    /// would otherwise only show up as a count nobody compares.
    /// </summary>
    [Fact]
    public void The_managed_enum_is_the_same_set()
    {
        var managed = Enum.GetValues<Ghostty.Core.Config.ConfigFilesFound>()
            .ToDictionary(v => v.ToString().ToUpperInvariant(), v => (int)v);

        Assert.Equal(
            ReadHeaderEnum().OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
            managed.OrderBy(p => p.Key, StringComparer.Ordinal).ToList());
    }

    // `unreadable = -1,` and the two beside it, from the declaration down to
    // the first method on the type: the members are all above that, and the
    // nested `Result` type below it has fields that would otherwise read as
    // more members.
    private static Dictionary<string, int> ReadZigEnum()
    {
        var text = Read(ZigResource);
        var open = text.IndexOf(ZigDecl, StringComparison.Ordinal);
        Assert.True(open >= 0, $"'{ZigDecl}' not found in src/config/Config.zig");

        var close = text.IndexOf("\n    pub fn ", open, StringComparison.Ordinal);
        Assert.True(close >= 0, "no method follows DefaultFiles' members in src/config/Config.zig");

        var members = Regex.Matches(
                text[(open + ZigDecl.Length)..close],
                @"^\s{4}(?<name>[a-z_]+) = (?<value>-?\d+),\s*$",
                RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["name"].Value.ToUpperInvariant(),
                          m => int.Parse(m.Groups["value"].Value));

        // Load-bearing: a declaration that stopped matching reads as an enum
        // with no members, and every comparison against it passes trivially
        // once the other side is empty too.
        Assert.NotEmpty(members);
        return members;
    }

    // `typedef enum { ... } ghostty_config_default_files_e;`, with the
    // prefix stripped. Located from the closing line backwards: the opening
    // `typedef enum {` is not unique in this header.
    private static Dictionary<string, int> ReadHeaderEnum()
    {
        var text = Read(HeaderResource);
        var close = text.IndexOf("} " + Typedef + ";", StringComparison.Ordinal);
        Assert.True(close >= 0, $"{Typedef} not found in include/ghostty.h");

        var open = text.LastIndexOf("typedef enum {", close, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no enum body precedes {Typedef} in include/ghostty.h");

        var members = Regex.Matches(
                text[(open + "typedef enum {".Length)..close],
                @"^\s*" + Prefix + @"(?<name>[A-Z_]+) = (?<value>-?\d+),\s*$",
                RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["name"].Value, m => int.Parse(m.Groups["value"].Value));

        Assert.NotEmpty(members);
        return members;
    }

    private static string Read(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        Assert.True(stream is not null, $"{resource} is not embedded; see Ghostty.Tests.csproj");
        using var reader = new System.IO.StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
