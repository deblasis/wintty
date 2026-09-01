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
        var core = typeof(Ghostty.Core.Tabs.TabModel).Assembly;
        Assert.Null(core.GetType("Ghostty.Core.Demo.DemoScriptParser", throwOnError: false));
        Assert.Null(core.GetType("Ghostty.Core.Demo.DemoActions", throwOnError: false));
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
    /// </summary>
    [Fact]
    public void Each_gate_has_an_opt_in_constant_keyed_to_its_own_opt_in()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(
            "Ghostty.Tests.Build.Directory.Build.targets")!;
        var doc = System.Xml.Linq.XDocument.Load(stream);

        AssertOptIn(doc, "'$(Demo)' == 'true'", "DEMO_OPTIN");
        AssertOptIn(doc, "'$(TestSeam)' == 'true'", "TESTSEAM_OPTIN");
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
