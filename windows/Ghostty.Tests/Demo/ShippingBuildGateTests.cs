using System;
using System.Linq;
using Xunit;

namespace Ghostty.Tests.Demo;

/// <summary>
/// The negative direction of the DEMO and TESTSEAM gates (issue #927).
///
/// Every other check on these gates asserts the property is WRITTEN correctly
/// in windows/Directory.Build.targets. None asserted that a shipping build
/// does not carry what the gate excludes, so a value set from anywhere else
/// turned the gate off with every guard still green:
///
///   dotnet build Ghostty.csproj -c Release /p:DemoEnabled=true
///     -> DEMO defined, DemoOverlay.xaml back in Page, DemoGateTests passing
///
/// The primary guard against that is the RefuseAGateLeakIntoARelease target,
/// which fails the build itself and reaches the app project these tests
/// cannot: neither test project references Ghostty.csproj, so nothing here
/// can see whether Wintty.dll carries the seam.
///
/// This is the second line, and it covers a route the target cannot see: a
/// `DefineConstants;DEMO` written directly into a project file never sets
/// DemoEnabled, so the build-time error never fires, and only the compiled
/// result gives it away.
///
/// Scoped to the configurations where the claim is true. In Debug both
/// constants are supposed to be present, and under the documented opt-ins
/// (-p:Demo=true / -p:TestSeam=true, which define DEMO_OPTIN / TESTSEAM_OPTIN)
/// a Release build carrying them is exactly what was asked for.
/// </summary>
public class ShippingBuildGateTests
{
#if !DEBUG && !DEMO_OPTIN
    [Fact]
    public void A_shipping_build_carries_no_demo_code()
    {
#if DEMO
        Assert.Fail(
            "DEMO is defined in a Release build that did not pass -p:Demo=true. "
            + "Demo sources are compiled into a binary users install. Check for a "
            + "DefineConstants or DemoEnabled set outside windows/Directory.Build.targets.");
#endif

        // The constant is only half of it: the gate also strips the sources,
        // and a build that defined neither but compiled them anyway would
        // still ship demo code. Ghostty.Core is the half these tests can
        // reach, and it is where the demo logic lives.
        //
        // The whole namespace, not a list of names. A hand-maintained list is
        // how the eighth demo type ships while the scan reports clean, which
        // is the warning Ghostty.Tests.csproj already gives twice about its
        // own source globs. GetTypes() rather than GetType(name) because the
        // claim is "nothing here", which a lookup cannot make; Ghostty.Core is
        // plain net10.0 with no optional dependencies for it to trip over.
        var core = typeof(Ghostty.Core.Tabs.TabModel).Assembly;
        var demoTypes = core.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Ghostty.Core.Demo", StringComparison.Ordinal) == true)
            .Select(t => t.FullName)
            .ToList();

        Assert.True(
            demoTypes.Count == 0,
            "a shipping build carries Ghostty.Core.Demo types: "
            + string.Join(", ", demoTypes));
    }
#endif

#if !DEBUG && !TESTSEAM_OPTIN
    [Fact]
    public void A_shipping_build_carries_no_test_seam()
    {
#if TESTSEAM
        Assert.Fail(
            "TESTSEAM is defined in a Release build that did not pass "
            + "-p:TestSeam=true. The seam is a named pipe whose send-text op hands "
            + "arbitrary bytes to a live shell, so a build carrying it must not be "
            + "installed. Check for a DefineConstants or TestSeamEnabled set outside "
            + "windows/Directory.Build.targets.");
#endif
        // Deliberately no reflection half. The seam lives in the app assembly
        // and no test project references it, so this leg speaks only for the
        // constant reaching a project under windows/ -- which is the shared
        // property, and therefore evidence about the app only insofar as the
        // property is shared. RefuseAGateLeakIntoARelease is what actually
        // binds Ghostty.csproj, and it binds it at build time.
        Assert.True(true);
    }
#endif

    /// <summary>
    /// The opt-in constants have to keep existing, or both facts above
    /// silently stop running: they are wrapped in <c>#if !DEMO_OPTIN</c> and
    /// <c>#if !TESTSEAM_OPTIN</c>, so a constant that is never defined leaves
    /// them permanently on rather than permanently off, but a constant that is
    /// RENAMED in the build file leaves the opt-in builds failing a test they
    /// should be exempt from.
    ///
    /// Read from the build file rather than from this assembly's constants,
    /// for the same reason the primary gate guard is: a `#if DEMO_OPTIN`
    /// assertion cannot see a misspelling, because a misspelled constant is
    /// simply never defined and the assertion compiles to nothing.
    ///
    /// It catches a rename in the BUILD FILE, which is the likely direction.
    /// It cannot cross-check the literal in this file's own `#if !DEMO_OPTIN`
    /// wrappers; nothing can, short of a run with the opt-in taken.
    /// </summary>
    [Fact]
    public void Each_gate_has_an_opt_in_constant_keyed_to_its_own_opt_in()
    {
        var doc = BuildTargets();
        AssertOptIn(doc, "'$(Demo)' == 'true'", "DEMO_OPTIN");
        AssertOptIn(doc, "'$(TestSeam)' == 'true'", "TESTSEAM_OPTIN");
    }

    /// <summary>
    /// The build-time refusal, read as text so it is checkable from a DEBUG
    /// run.
    ///
    /// It has to be. The target only evaluates its conditions when
    /// Configuration is not Debug, and the signoff ladder's windows-tests leg
    /// is `just test-win`, which passes no -c and is therefore Debug. Without
    /// this, an inverted condition in either Error would reach `windows`
    /// green and only surface in the tiered build's Release cells, which run
    /// against a pin that lags. That is the guard-that-does-not-run shape
    /// this whole change is about, one level up from where it started.
    /// </summary>
    [Fact]
    public void The_build_time_refusal_still_refuses_both_gates()
    {
        var doc = BuildTargets();

        var target = Assert.Single(
            doc.Descendants(),
            e => e.Name.LocalName == "Target"
                 && (string?)e.Attribute("Name") == "RefuseAGateLeakIntoARelease");

        // Runs at all, and on a hook that is not skipped for an up-to-date
        // project. CoreCompile alone was exactly that bug.
        var before = (string?)target.Attribute("BeforeTargets") ?? string.Empty;
        Assert.Contains("BeforeBuild", before, StringComparison.Ordinal);

        // Guards every configuration that is not Debug, rather than naming
        // Release, so a configuration nobody has added yet is on the guarded
        // side.
        Assert.Equal("'$(Configuration)' != 'Debug'", (string?)target.Attribute("Condition"));

        AssertRefusal(target, "WINTTY0001", "TestSeamEnabled", "_TestSeamOptInIsGlobal");
        AssertRefusal(target, "WINTTY0002", "DemoEnabled", "_DemoOptInIsGlobal");
    }

    /// <summary>
    /// One Error, asserted on its polarity. `'$(X)' != 'true'` inverted to
    /// `== 'true'` turns the refusal into a rubber stamp, and the two halves
    /// have opposite senses, so both are spelled out rather than matched
    /// loosely.
    /// </summary>
    private static void AssertRefusal(
        System.Xml.Linq.XElement target, string code, string gate, string optInFlag)
    {
        var error = Assert.Single(
            target.Descendants(),
            e => e.Name.LocalName == "Error" && (string?)e.Attribute("Code") == code);

        Assert.Equal(
            $"'$({gate})' == 'true' and '$({optInFlag})' != 'true'",
            (string?)error.Attribute("Condition"));
    }

    private static System.Xml.Linq.XDocument BuildTargets()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(
            "Ghostty.Tests.Build.Directory.Build.targets")!;
        return System.Xml.Linq.XDocument.Load(stream);
    }

    private static void AssertOptIn(
        System.Xml.Linq.XDocument doc, string condition, string constant)
    {
        var define = Assert.Single(
            doc.Descendants(),
            e => e.Name.LocalName == "DefineConstants"
                 && e.Attribute("Condition")?.Value == condition);

        // As a token, not a substring. DefineConstants is a semicolon list and
        // `Contains("DEMO_OPTIN")` is satisfied by "DEMO_OPTIN_TYPO", so the
        // substring form passes the exact rename it exists to catch.
        var defined = define.Value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim());
        Assert.Contains(constant, defined);
    }
}
