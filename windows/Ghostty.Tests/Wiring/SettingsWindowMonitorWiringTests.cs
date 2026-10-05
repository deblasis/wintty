using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That the settings window opens on the monitor the request came from.
///
/// It did not: the constructor resolved its own
/// <c>DisplayArea.GetFromWindowId(windowId, Primary)</c>, and a window that
/// has never been shown sits wherever the OS put a fresh one -- the primary
/// display -- so a user with the terminal on a second monitor and the
/// primary as a 4K panel got a settings window on the wrong screen, sized
/// for the wrong DPI and behind every other window on the primary.
///
/// The choice this pins is the mechanism, because both of the brief's
/// candidates answer differently when there is no caller. The settings ask
/// arrives as the app-targeted <c>open_config</c> action, whose event carries
/// no window: libghostty raises it on the process-wide bootstrap host, so
/// there is nothing to take an owner from. The foreground window at the
/// moment the constructor runs IS the requesting window -- a keybind, a
/// palette entry and a tray menu all fire while the terminal that owns them
/// still holds the foreground, and the settings window has not been shown
/// yet, so it cannot be what this names. So: the foreground window's id
/// goes through the same WinAppSDK call the old line used, with
/// <c>DisplayAreaFallback.Primary</c> underneath it for the two ways there
/// is no foreground window to ask about.
///
/// The WinUI assembly cannot be loaded into a test host, so this reads the
/// source. What it cannot prove is which monitor a real foreground window
/// sits on.
/// </summary>
public class SettingsWindowMonitorWiringTests
{
    /// <summary>Where the monitor choice is made.</summary>
    private const string HelperFile = "Branding.WindowHelper.cs";

    /// <summary>The settings shell, whose ctor consumes the choice.</summary>
    private const string SettingsFile = "Settings.SettingsWindow.xaml.cs";

    /// <summary>The resolver the ctor has to ask.</summary>
    private const string Resolver = "WorkAreaForCaller";

    [Fact]
    public void TheCallerDisplayComesFromTheForegroundWindow()
    {
        var source = ShellSource.Load(HelperFile);
        var method = source.Method(Resolver);

        // One read of the foreground window, and it is what the placement is
        // resolved against.
        var foreground = Assert.Single(method.Calls("PInvoke.GetForegroundWindow"));
        Assert.Equal(0, foreground.ArgumentList.Arguments.Count);

        // The handle comes out of HWND through a cast, so the read is a
        // descendant of the initializer rather than the initializer itself.
        var handle = Assert.Single(
            method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Initializer is { } init
                            && init.DescendantNodesAndSelf()
                                .OfType<InvocationExpressionSyntax>()
                                .Any(i => i.CalleeText() == "PInvoke.GetForegroundWindow")));

        // The id handed to DisplayArea is the foreground window's, and only
        // where there is one: a zero handle (a tray-only ask during logon)
        // takes the caller's own fallback instead, which is the primary.
        var condition = Assert.Single(
            method.DescendantNodes().OfType<ConditionalExpressionSyntax>());
        var fallback = Assert.Single(
            method.ParameterList.Parameters,
            p => p.Type?.ToString() == "WindowId");
        Assert.Equal(
            fallback.Identifier.ValueText,
            condition.WhenTrue.ToString().Trim());
        Assert.Equal(
            $"Win32Interop.GetWindowIdFromWindow({handle.Identifier.ValueText})",
            condition.WhenFalse.ToString().Trim());

        var display = Assert.Single(method.Calls("DisplayArea.GetFromWindowId"));
        Assert.True(
            display.ArgumentList.Arguments.Count == 2,
            "DisplayArea.GetFromWindowId takes the window id and the fallback "
            + $"preference, and both matter here; found {display.ArgumentList.Arguments.Count}");
        Assert.Equal(
            "DisplayAreaFallback.Primary",
            display.Arg(1));
        // The lookup is made against a LOCAL that holds the conditional, not
        // against the conditional inline: matching the node shape here would
        // pass on `GetFromWindowId(cond ? a : b, Primary)` and fail on the
        // two-line form that names the choice, while the argument that
        // actually matters -- that it is this id and not the fallback alone
        // -- is the same either way.
        var id = Assert.Single(
            method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Identifier.ValueText == "id"
                            && v.Initializer?.Value is ConditionalExpressionSyntax));
        Assert.Equal(condition, id.Initializer!.Value);
        Assert.True(
            display.ArgExpression(0).DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Any(i => i.Identifier.ValueText == "id"),
            $"the DisplayArea lookup must be made against the foreground window's "
            + $"id, found `{display.Arg(0)}`");
    }

    [Fact]
    public void TheSettingsWindowAsksForTheCallerDisplay()
    {
        var ctor = Assert.Single(
            ShellSource.Load(SettingsFile).Root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>());

        var asks = ctor.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith(Resolver, StringComparison.Ordinal))
            .ToList();
        var ask = Assert.Single(asks);
        Assert.Equal(
            $"WindowHelper.{Resolver}",
            ask.CalleeText());
        Assert.Equal(
            "windowId",
            ask.Arg(0));

        // And nothing in that constructor resolves a display area off the
        // window's own id any more: that is the line this replaces, and it
        // is the whole defect.
        var stale = ctor.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "DisplayArea.GetFromWindowId")
            .ToList();
        Assert.True(
            stale.Count == 0,
            "the settings constructor still resolves a DisplayArea itself "
            + $"({stale.Count} left); a window that has never been shown resolves "
            + "to the primary display whatever the caller wanted");
    }
}
