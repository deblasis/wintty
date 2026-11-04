using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The scheme transaction's shape: the config is built and gated before any
/// conditional state moves, the commit moves the app and every live surface
/// before the renderer sees the config, and a soft reload is decoded apart
/// from a real one. These are the properties the seam harness relies on when
/// it forces a scheme with no desktop change.
/// </summary>
public sealed class ThemeSchemeWiringTests
{
    private static ShellSource Service() => ShellSource.Load("Services.ConfigService.cs");

    [Fact]
    public void TheTransactionDoesNotTouchTheSchemeBeforeTheGate()
    {
        // ApplyPendingScheme delegates to ReloadWithScheme, which owns the
        // gates. A direct AppSetColorScheme here would move the app state
        // even when the build or the gate declines.
        var apply = Service().Method("ApplyPendingScheme");
        Assert.Empty(apply.Calls("Ghostty.Interop.NativeMethods.AppSetColorScheme"));
        Assert.Empty(apply.Calls("Ghostty.App.NotifyAllSurfacesColorScheme"));
        Assert.NotEmpty(apply.Calls("ReloadWithScheme"));
    }

    [Fact]
    public void TheSchemeCommitsBeforeTheRendererGetsTheConfig()
    {
        // App.updateConfig applies the app's conditional state to the config
        // it is handed, and every surface applies its own. Both have to be
        // on the target scheme before AppUpdateConfig, or a surface replays
        // the outgoing half against the incoming config.
        var reload = Service().Method("ReloadWithScheme");
        var app = reload.CallsEndingWith("AppSetColorScheme").Single();
        var surfaces = reload.CallsEndingWith("NotifyAllSurfacesColorScheme").Single();
        var update = reload.CallsEndingWith("AppUpdateConfig").Single();

        Assert.True(app.SpanStart < update.SpanStart,
            "AppSetColorScheme must precede AppUpdateConfig");
        Assert.True(surfaces.SpanStart < update.SpanStart,
            "every surface's scheme must precede AppUpdateConfig");
    }

    [Fact]
    public void TheHighContrastSecondBuildIsTheChromePairOnly()
    {
        // The pre-HC build is a second BuildLiveConfig with a null palette;
        // it must be freed in the same scope and never stored.
        var reload = Service().Method("ReloadWithScheme");
        var builds = reload.Calls("BuildLiveConfig").ToList();
        Assert.Equal(2, builds.Count);
        Assert.Contains("highContrast: null", builds[1].ToString());

        var free = reload.CallsEndingWith("ConfigFree");
        Assert.True(free.Any(c => c.Arg(0) == "preHighContrast"),
            "the pre-HC handle must be freed where it is read");
    }

    [Fact]
    public void ADuplicateObservationOfADeclinedTargetDoesNotRetry()
    {
        // The OS raises the event once per window and for accent changes;
        // each duplicate would otherwise build the config again and pay a
        // held file's loader retry.
        var apply = Service().Method("ApplyPendingScheme");
        var declined = apply.DescendantNodes().OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.ValueText == "_declinedDark");
        var reload = apply.Calls("ReloadWithScheme").Single();
        Assert.True(declined.SpanStart < reload.SpanStart,
            "the declined-target guard must precede ReloadWithScheme");
    }

    [Fact]
    public void TheReloadSamplesBeforeRetryingADeclinedTarget()
    {
        // A flip back whose event was missed must not apply a stale target;
        // the desktop is sampled first, and only a differing observation
        // runs the transaction.
        var reload = Service().Method("Reload");
        var sample = reload.Calls("OsTheme.IsDark").Single();
        var declined = reload.DescendantNodes().OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.ValueText == "_declinedDark");
        Assert.True(sample.SpanStart < declined.SpanStart,
            "Reload must sample the desktop before the declined-target branch");
    }

    [Fact]
    public void TheManagerHasNoDesktopSampleOfItsOwn()
    {
        var source = ShellSource.Load("Services.WindowThemeManager.cs").Root.ToString();
        Assert.DoesNotContain("OsTheme.IsDark", source);
        Assert.DoesNotContain("ColorValuesChanged", source);
        Assert.Contains("AppliedDark", source);
    }

    [Fact]
    public void ResolveNowIsSilent()
    {
        var method = ShellSource.Load("Services.WindowThemeManager.cs").Method("ResolveNow");
        Assert.DoesNotContain("ThemeChanged", method.ToString());
    }

    [Fact]
    public void ThePaletteActiveThemeUsesTheAppliedHalf()
    {
        var source = ShellSource.Load("MainWindow.xaml.cs").Root.ToString();
        Assert.Contains("SelectForScheme(", source);
        Assert.Contains("_configService.AppliedDark", source);
    }

    [Fact]
    public void TheSoftReloadIsDecodedApartFromTheRealOne()
    {
        var host = ShellSource.Load("Hosting.GhosttyHost.cs").Root;
        var arm = host.DescendantNodes().OfType<CaseSwitchLabelSyntax>()
            .First(c => c.Value.ToString() == "GhosttyActionTag.ReloadConfig")
            .Ancestors().OfType<SwitchSectionSyntax>().First();

        Assert.Contains("GhosttyActionTag.ReloadConfig", arm.ToString());
        Assert.Contains("SoftReloadRequested", arm.ToString());
        Assert.Contains("ReloadConfigRequested", arm.ToString());
        Assert.Contains("Marshal.ReadByte", arm.ToString());
    }
}
