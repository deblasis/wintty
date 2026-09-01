using System;
using System.Linq;
using Xunit;

namespace Ghostty.Tests.Demo;

/// <summary>
/// The demo gate.
///
/// It used to live in <c>windows/Directory.Build.props</c>, where
/// <c>Configuration</c> is still empty: <c>Microsoft.Common.props</c> imports
/// that file before it defaults <c>Configuration</c> to <c>Debug</c>. The
/// condition therefore matched only when the value arrived as a global
/// property. Visual Studio and anything passing <c>-c</c> supplied one;
/// <c>just build-win</c> and <c>just test-win</c> pass only
/// <c>/p:Platform=x64</c>, so DEMO went undefined for every just recipe and
/// so for the signoff ladder's windows-tests leg. Three test classes compiled
/// to nothing and the ladder reported green.
///
/// The primary guard reads the BUILD FILE, not this assembly's constants, and
/// that is deliberate. A guard asserting `#if DEMO` here can only speak about
/// the configuration it was itself compiled in, so it would fail every
/// Release test run -- and the tiered build runs <c>dotnet test -c Release</c>
/// across five cells, which the OSS ladder never exercises. A gate whose
/// guard breaks a configuration the ladder cannot see is the same blind spot
/// this whole change exists to close, one level up.
///
/// Same shape, and the same reasoning, as <c>TestSeamWiringTests</c>'s check
/// on the seam gate three lines above it in that file.
/// </summary>
public class DemoGateTests
{
    private static System.Xml.Linq.XDocument BuildTargets()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(
            "Ghostty.Tests.Build.Directory.Build.targets")!;
        return System.Xml.Linq.XDocument.Load(stream);
    }

    [Fact]
    public void The_demo_gate_reads_a_settled_Configuration()
    {
        var doc = BuildTargets();

        // In Directory.Build.targets at all: that is the fix. In
        // Directory.Build.props the condition below is evaluated against an
        // empty Configuration and silently never matches.
        var enabled = Assert.Single(
            doc.Descendants(), e => e.Name.LocalName == "DemoEnabled");

        var condition = enabled.Attribute("Condition")?.Value;
        Assert.False(
            string.IsNullOrWhiteSpace(condition),
            "DemoEnabled has no Condition, so every build defines DEMO and a "
            + "public Release ships demo code.");

        // Debug, or an explicit opt-in, and nothing else. A condition that
        // also admits Release is the gate gone the other way.
        Assert.Contains("'$(Configuration)' == 'Debug'", condition!, StringComparison.Ordinal);
        Assert.Contains("'$(Demo)' == 'true'", condition!, StringComparison.Ordinal);
        Assert.DoesNotContain("Release", condition!, StringComparison.Ordinal);

        // And the symbol is defined only when that property says so.
        var define = Assert.Single(
            doc.Descendants(),
            e => e.Name.LocalName == "DefineConstants"
                 && (e.Attribute("Condition")?.Value.Contains("DemoEnabled", StringComparison.Ordinal) ?? false));
        Assert.Contains("DEMO", define.Value, StringComparison.Ordinal);
    }

#if DEBUG
    /// <summary>
    /// The build-file check proves the gate is written correctly; this proves
    /// it arrived. Scoped to DEBUG because that is the configuration whose
    /// invariant it states -- in Release both of these SHOULD be absent, and
    /// asserting them there is what broke the tiered Release test leg.
    /// </summary>
    [Fact]
    public void A_Debug_build_actually_carries_the_constant()
    {
#if DEMO
        // Reached only if DEMO survived to this assembly's compile.
        Assert.True(true);
#else
        Assert.Fail(
            "This is a DEBUG build and DEMO is not defined, so the demo tests "
            + "compiled to nothing and every assertion about demo behaviour is "
            + "vacuous. Check DemoEnabled in windows/Directory.Build.targets.");
#endif

        // A constant defined only for the test assembly would leave the code
        // under test compiled out while these tests still ran -- green, and
        // measuring nothing. Directory.Build.targets sits above every project
        // under windows/, so a Core demo type is the check that it did.
        // GetType rather than GetTypes(): one type loaded, and no
        // ReflectionTypeLoadException to misreport as a missing gate.
        var core = typeof(Ghostty.Core.Tabs.TabModel).Assembly;
        Assert.True(
            core.GetType("Ghostty.Core.Demo.DemoScriptParser", throwOnError: false) is not null,
            "Ghostty.Core carries no Ghostty.Core.Demo.DemoScriptParser, so DEMO "
            + "reached this assembly but not Core: something has scoped DemoEnabled "
            + "below windows/.");
    }
#endif
}
