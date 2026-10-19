using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Wiring;

// Pins the terminfo sentinel that ThemeSearchPath probes for to the zig file
// that owns it. src/terminfo/paths.zig derives every install path and runtime
// sentinel from the entry names, and the zig side fails the build when the
// table or the installers drift from it. C# cannot import it, so this literal
// had no guard at all: renaming the entry moved every zig spelling and left
// HasTerminfoSentinel probing a path nothing installs, silently refusing
// valid resources trees at run time. These tests are the pin for that edit.
//
// Both files arrive as embedded resources captured when Ghostty.Tests builds,
// not read from disk at run time, so an edit that does not rebuild this
// project shows the tests a stale copy.
public class ThemeSearchPathTerminfoWiringTests
{
    private const string PathsResourceName = "Ghostty.Tests.Terminfo.paths.zig";

    /// ThemeSearchPath.cs arrives through the Ghostty.Core source wildcard in
    /// Ghostty.Tests.csproj, so its full resource name is whatever that glob
    /// produced; resolve it by suffix the way the other source-reading tests
    /// do rather than restating the path.
    private static string ThemeResourceName() => ResolveResource("Config.ThemeSearchPath.cs");

    private static string ResolveResource(string dottedTail)
    {
        // MSBuild substitutes the host's directory separator into the
        // logical name, so neither "\" nor "/" can be assumed. Fold both
        // to dots before matching.
        var asm = Assembly.GetExecutingAssembly();
        var matches = asm.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '.').Replace('/', '.')
                .EndsWith("." + dottedTail, StringComparison.Ordinal))
            .ToList();
        Assert.True(
            matches.Count == 1,
            $"expected exactly one embedded resource ending in '{dottedTail}', found " +
            $"{matches.Count}: " + string.Join(", ", matches));
        return matches[0];
    }

    private static readonly Regex CommentRegex = new(@"(?s)<!--.*?-->", RegexOptions.Compiled);

    /// <summary>
    /// The string literal assigned to a pub const in paths.zig. Comptime
    /// concatenations are deliberately not followed: only the plain literals
    /// are matched, so a refactor that makes a value derived stops the test
    /// loudly instead of quietly weakening it.
    /// </summary>
    private static string PathsConst(string name)
    {
        var paths = ReadEmbeddedText(PathsResourceName);
        var match = Regex.Match(
            paths,
            @"pub const " + name + @" = ""(?<value>[^""]+)"";");
        Assert.True(match.Success, $"paths.zig has no plain string literal for pub const {name}; " +
            "the sentinel probe cannot be pinned to a derived value.");
        return match.Groups["value"].Value;
    }

    private static string SourceFileSuffix()
    {
        var paths = ReadEmbeddedText(PathsResourceName);
        var match = Regex.Match(
            paths,
            @"pub const source_file = preferred_name \+\+ ""(?<suffix>[^""]+)"";");
        Assert.True(match.Success,
            "paths.zig no longer builds source_file from preferred_name plus a literal suffix.");
        return match.Groups["suffix"].Value;
    }

    [Fact]
    public void HasTerminfoSentinelProbesTheDerivedWindowsSentinel()
    {
        var theme = ReadEmbeddedText(ThemeResourceName());

        // The probe: Path.Combine(parent, <db_dir>, <source file>) inside
        // HasTerminfoSentinel. Matched on the method body so another
        // Path.Combine elsewhere in the file cannot satisfy it.
        var body = MethodBlock(theme, "HasTerminfoSentinel");
        var probe = Regex.Match(
            body,
            @"Path\.Combine\(parent,\s*""(?<dir>[^""]+)"",\s*""(?<file>[^""]+)""\)");
        Assert.True(probe.Success,
            "HasTerminfoSentinel no longer combines a db-dir and a file literal; " +
            "the pin to paths.zig needs updating, not deleting.");

        Assert.Equal(PathsConst("db_dir"), probe.Groups["dir"].Value);
        Assert.Equal(PathsConst("preferred_name") + SourceFileSuffix(), probe.Groups["file"].Value);
    }

    private static string MethodBlock(string source, string name)
    {
        var start = source.IndexOf(
            $"private static bool {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"ThemeSearchPath.cs has no method {name}.");
        var end = source.IndexOf('}', start);
        Assert.True(end > start, $"ThemeSearchPath.cs: method {name} has no body.");
        return source.Substring(start, end - start);
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
