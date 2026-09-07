using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The order the config is assembled in, and the reads --no-config has to
/// reach.
///
/// Order is not style here. libghostty resets config-default-files at the
/// top of loadCliArgs and rebuilds from a replay marker taken there, so the
/// discard can only drop what was loaded before it: put the CLI first and
/// --no-config becomes a silent no-op that still passes every behaviour
/// test, because the flag parses fine and simply discards nothing.
///
/// The two sites are the constructor and Reload. They have to agree, or a
/// reload quietly changes which configuration is in force.
/// </summary>
public class ConfigLoadOrderWiringTests
{
    private const string ConfigService = "Services.ConfigService.cs";

    // Upstream's Config.load: default files, CLI args, the files those name,
    // then finalize. SetColorScheme is Wintty's and has to stay ahead of
    // finalize, which is where the theme is applied.
    private static readonly string[] ExpectedOrder =
    {
        "NativeMethods.ConfigNew",
        "NativeMethods.ConfigLoadDefaultFiles",
        "NativeMethods.ConfigLoadCliArgs",
        "NativeMethods.ConfigLoadRecursiveFiles",
        "NativeMethods.ConfigSetColorScheme",
        "NativeMethods.ConfigFinalize",
    };

    /// <summary>
    /// The assembly calls under <paramref name="node"/>, in source order,
    /// restricted to the ones the order rule is about. Position comes from
    /// the span, so wrapping a call in a block or an if does not reorder it.
    /// </summary>
    private static List<string> LoadSequence(SyntaxNode node) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.CalleeText())
            .Where(name => ExpectedOrder.Contains(name))
            .ToList();

    [Theory]
    [InlineData("ConfigService")]
    [InlineData("Reload")]
    public void BothSitesAssembleTheConfigInUpstreamOrder(string member)
    {
        var source = ShellSource.Load(ConfigService);

        // The constructor is not a MethodDeclaration, so it is found by its
        // own node kind rather than through ShellSource.Method.
        SyntaxNode body = member == "ConfigService"
            ? source.Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Single()
            : source.Method(member);

        Assert.Equal(ExpectedOrder, LoadSequence(body));
    }

    /// <summary>
    /// High Contrast is an accessibility override and outranks everything the
    /// user asked for, a file named with --config-file included. That is only
    /// true while it loads after the recursive files.
    /// </summary>
    [Fact]
    public void HighContrastOverrideLoadsAfterEverythingElse()
    {
        var reload = ShellSource.Load(ConfigService).Method("Reload");

        var recursive = reload.Call("NativeMethods.ConfigLoadRecursiveFiles");
        var hc = reload.Call("NativeMethods.ConfigLoadFile");
        var finalize = reload.Call("NativeMethods.ConfigFinalize");

        Assert.True(
            recursive.SpanStart < hc.SpanStart,
            "the High Contrast override loads before the config-file includes, so a "
                + "file named on the command line would overwrite it");
        Assert.True(
            hc.SpanStart < finalize.SpanStart,
            "the High Contrast override loads after finalize, where it cannot take effect");
    }

    /// <summary>
    /// Every read that puts the config file's contents into force goes
    /// through ConfigSourcePath, which is null under --no-config. Naming
    /// ConfigFilePath at one of them is how the flag silently stops being
    /// total: that property stays populated on purpose, for the raw editor
    /// and the theme search path.
    /// </summary>
    [Fact]
    public void TheConfigFileCacheIsBuiltFromTheSuppressiblePath()
    {
        var readFlags = ShellSource.Load(ConfigService).Method("ReadFlags");

        var assignment = readFlags.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "_configFileCache");

        // Not Call("LoadIniFile"): ReadFlags loads the theme file through the
        // same helper, and that one is meant to keep reading.
        var load = Assert.IsType<InvocationExpressionSyntax>(assignment.Right);
        Assert.Equal("LoadIniFile", load.CalleeText());
        Assert.Equal("ConfigSourcePath", load.Arg(0));
    }

    /// <summary>
    /// The profile keys used to come from a second raw-text read of the
    /// config file (<c>File.ReadAllText</c>), a third read of the same file
    /// on top of the native ConfigNew..ConfigFinalize read and the managed
    /// <c>_configFileCache = LoadIniFile(...)</c> read that ReadFlags already
    /// does. That call is gone: the profile pass now consumes the same
    /// pairs cache <c>GetFileValue</c> and friends already read from, so a
    /// reload is bounded by the native read plus the two <c>LoadIniFile</c>
    /// calls ReadFlags documents (config file, active theme file), not a
    /// third file open.
    ///
    /// Scanning the whole file rather than just ReadFlagsCore, so a
    /// File.ReadAllText that migrates to some other method in this class
    /// still trips the guard.
    /// </summary>
    [Fact]
    public void TheProfilePassReadsFromTheSharedCacheNotAThirdFileRead()
    {
        var source = ShellSource.Load(ConfigService);

        Assert.Empty(source.Root.Calls("File.ReadAllText"));

        var core = source.Method("ReadFlagsCore");
        var cacheReferences = core.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == "_configFileCache")
            .ToList();
        Assert.True(
            cacheReferences.Count > 0,
            "ReadFlagsCore no longer references _configFileCache -- the profile pass "
                + "has to read the same cache ReadFlags populated, not some other source.");
    }

    /// <summary>
    /// The watcher must not arm under --no-config. Watching a file we are
    /// deliberately ignoring, then reloading from it, is how a save would
    /// undo the flag.
    /// </summary>
    [Fact]
    public void TheWatcherDoesNotArmUnderNoConfig()
    {
        var start = ShellSource.Load(ConfigService).Method("StartWatcher");

        var guard = start.Body!.Statements.OfType<IfStatementSyntax>()
            .FirstOrDefault(s => s.Condition.ToString() == "_noConfig");

        Assert.True(
            guard is not null,
            "StartWatcher has no `if (_noConfig) return;` guard, so a save to the "
                + "ignored config file would reload from it");
        Assert.Equal("return;", guard!.Statement.ToString());
    }

    /// <summary>
    /// The one flag every read site consults is assigned from the one
    /// process-wide reading, not re-derived from the command line here. Two
    /// derivations are two answers, and libghostty is holding the config that
    /// matches only one of them.
    /// </summary>
    [Fact]
    public void SuppressionComesFromTheProcessWideReading()
    {
        var ctor = ShellSource.Load(ConfigService).Root
            .DescendantNodes().OfType<ConstructorDeclarationSyntax>().Single();

        var assignment = ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_noConfig")
            .ToList();

        Assert.True(assignment.Count == 1, $"expected one assignment to _noConfig, found {assignment.Count}");
        Assert.Equal("Program.ConfigOverrides.NoConfig", assignment[0].Right.ToString());
    }

    /// <summary>
    /// Program's own pre-Application.Start read of windows-single-instance
    /// bypasses ConfigService entirely, so it needs the suppression applied
    /// at its own call. Left out, --no-config routes the launch into a
    /// primary holding the config it was told to ignore.
    /// </summary>
    [Fact]
    public void TheEarlySingleInstanceReadIsSuppressedToo()
    {
        var method = ShellSource.Load("Program.cs").Method("ReadSingleInstanceSetting");

        var load = method.Call("ConfigIniFile.Load");
        Assert.Contains("ConfigOverrides.NoConfig", load.Arg(0));
    }

    /// <summary>
    /// A launch that names a config the running primary cannot be holding has
    /// to become its own process. The election is what enforces that.
    /// </summary>
    [Fact]
    public void ConfigOverridesForceANewInstance()
    {
        var startGui = ShellSource.Load("Program.cs").Method("StartGui");

        var gated = startGui.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(s => s.Condition.ToString().Contains("ConfigOverrides.Any"))
            .ToList();

        Assert.True(gated.Count == 1, $"expected one ConfigOverrides.Any gate in StartGui, found {gated.Count}");

        var reelect = gated[0].Statement.Calls("SingleInstanceElection.Run");
        Assert.True(reelect.Count == 1, "the gate does not re-run the election");
        Assert.Contains("false", reelect[0].Arg(0));
    }
}
