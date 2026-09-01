using System;
using System.Linq;
using Xunit;

namespace Ghostty.Tests.Demo;

/// <summary>
/// The demo gate, asserted from inside the gate.
///
/// `DemoEnabled` is computed in windows/Directory.Build.props, where
/// Configuration is still empty because Microsoft.Common.props imports that
/// file before it defaults Configuration to Debug. For a long time the
/// condition read only `'$(Configuration)' == 'Debug'`, so DEMO was defined
/// from Visual Studio and from anything passing `-c`, and NOT from a bare
/// `dotnet build` -- which is what every `just` recipe issues, `test-win`
/// included, and therefore what the signoff ladder ran. Three test classes
/// compiled to nothing and the ladder called that green.
///
/// This file is deliberately NOT wrapped in `#if DEMO`. A self-gated test
/// vanishes with the thing it is testing, which is exactly the failure being
/// guarded against: the whole point is to fail loudly when the constant is
/// absent, not to disappear alongside it.
/// </summary>
public class DemoGateTests
{
    [Fact]
    public void The_DEMO_constant_reaches_this_assembly()
    {
#if DEMO
        Assert.True(true);
#else
        Assert.Fail(
            "DEMO is not defined for this build, so the demo tests compiled to "
            + "nothing and every other assertion about demo behaviour is vacuous. "
            + "Check DemoEnabled in windows/Directory.Build.props: Configuration is "
            + "empty there for a bare `dotnet build`, so the condition has to admit "
            + "the empty case as well as 'Debug'.");
#endif
    }

    [Fact]
    public void The_gate_reaches_Ghostty_Core_too()
    {
        // A constant defined only for the test assembly would leave the code
        // under test compiled out while these tests still ran -- green, and
        // measuring nothing. Directory.Build.props sits above every project
        // under windows/, so the Core demo types are the check that it did.
        var core = typeof(Ghostty.Core.Tabs.TabModel).Assembly;
        var demoTypes = core.GetTypes()
            .Where(t => t.Namespace == "Ghostty.Core.Demo")
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            demoTypes.Count > 0,
            "Ghostty.Core carries no Ghostty.Core.Demo types, so DEMO was not "
            + "defined when Core was compiled. If the constant test above passed, "
            + "the gate reaches the test assembly but not Core, and something has "
            + "scoped DemoEnabled below windows/. If it failed too, the gate is "
            + "off everywhere and its message is the one to read.");
    }
}
