using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That reopening the settings window restores it instead of only
/// activating it.
///
/// The defect is a Win32 truth rather than a C# one: <c>Activate</c> on a
/// minimized window brings the activation request to a window the shell
/// never shows, so the keystroke that asked for the settings is answered
/// with nothing on screen and the window stays in the taskbar. The
/// codebase already knows this and says so -- the shader gallery picker's
/// second entry point calls <c>AppWindow.Show()</c> and then
/// <c>Activate()</c>, with a comment naming the reason. The app-wide
/// settings window never got that half.
///
/// The WinUI assembly cannot be loaded into a test host, so this reads the
/// source. Three things are pinned and each is separately defeasible:
///
///   - The Show. An <c>AppWindow.Show()</c> anywhere in the method is not
///     enough; it has to be on the arm that runs when a window already
///     exists, which is the only path where a minimized window can be
///     waiting.
///   - The order. Show after Activate restores nothing: the window is
///     already activated and stays minimized.
///   - The single instance. The remedy must not become a second
///     construction, because two settings windows are two writers to one
///     config file and two search boxes over one page cache.
///
/// What this cannot prove: that the window really was minimized, or that
/// the OS honors the restore. Both need a live window.
/// </summary>
public class SettingsWindowRestoreWiringTests
{
    /// <summary>Where the one settings window lives.</summary>
    private const string AppFile = "Ghostty.App.xaml.cs";

    /// <summary>The app-wide entry point every ask funnels through.</summary>
    private const string Method = "ShowOrActivateSettings";

    /// <summary>The type that must never be constructed twice.</summary>
    private const string WindowType = "Ghostty.Settings.SettingsWindow";

    [Fact]
    public void AnExistingSettingsWindowIsRestoredAndThenActivated()
    {
        var body = ShellSource.Load(AppFile).Method(Method).Body!.Statements;

        // The arm that runs when one already exists. Top-level on purpose:
        // nested inside another branch it is a decision that may never be
        // reached, and the call queries below would still find both.
        var guards = body.OfType<IfStatementSyntax>()
            .Where(s => s.Condition.ToString().Contains("_settingsWindow", StringComparison.Ordinal))
            .ToList();
        var guard = Assert.Single(guards);
        Assert.True(
            guard.Statement.DescendantNodesAndSelf()
                .OfType<ReturnStatementSyntax>().Any(),
            $"the existing-window arm of {Method} must return; anything it falls "
            + "through to constructs a second settings window");

        // Show, on that arm, through the AppWindow.
        var shows = guard.Statement.Calls("_settingsWindow.AppWindow?.Show");
        var show = Assert.Single(shows);

        // Then activate. The reverse order is the defect wearing this fix's
        // own test green: an activated minimized window stays minimized.
        var activate = Assert.Single(guard.Statement.Calls("_settingsWindow.Activate"));
        Assert.True(
            show.SpanStart < activate.SpanStart,
            $"{Method} must restore before it activates. `Activate` alone does not "
            + "reliably restore a minimized window -- the codebase's own note on the "
            + "shader gallery picker says so -- so the Show has to come first or the "
            + "second open of a minimized settings window answers the keystroke with "
            + "nothing on screen");

        // And neither is behind a further branch, which would disable the
        // restore while leaving both calls right where this test looks.
        foreach (var call in new[] { show, activate })
        {
            var branch = call.Ancestors().TakeWhile(a => a != guard.Statement)
                .OfType<IfStatementSyntax>().FirstOrDefault();
            Assert.True(
                branch is null,
                $"{call} sits inside another branch of the existing-window arm; a "
                + "guard there that always holds disables the restore and leaves both "
                + "calls present");
        }
    }

    [Fact]
    public void ThereIsStillOnlyOneSettingsWindow()
    {
        var source = ShellSource.Load(AppFile);

        var built = source.Root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == WindowType)
            .ToList();
        var window = Assert.Single(built);
        Assert.True(
            window.ArgumentList.Arguments.Count == 4,
            $"the {WindowType} construction must keep its four arguments (config "
            + $"service, editor, keybindings, theme); found "
            + $"{window.ArgumentList.Arguments.Count}");

        // And it is still below the existing-window arm, not inside it.
        var method = source.Method(Method);
        var guard = Assert.Single(
            method.Body!.DescendantNodes().OfType<IfStatementSyntax>(),
            s => s.Condition.ToString().Contains("_settingsWindow", StringComparison.Ordinal));
        Assert.True(
            window.SpanStart > guard.Span.End,
            $"{Method} constructs the settings window inside the existing-window arm. "
            + "Restore-then-activate fixes a minimized reopen; it must not become a "
            + "second window, or the app grows one settings window per ask");
    }
}
