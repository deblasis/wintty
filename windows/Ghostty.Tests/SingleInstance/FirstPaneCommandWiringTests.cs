using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The shell side of #1136: every opener of a pane nobody picked a profile
/// for goes through <c>PaneCommandPolicy</c>, a <c>-e</c> launch neither
/// restores nor writes the session, and an argv reaches the surface as one.
/// The WinUI assembly cannot load in a test host, so what these pin is
/// shape; the rules themselves are pinned by <c>PaneCommandPolicyTests</c>,
/// and a launch-level check lives outside the repo for the verification
/// pass.
/// </summary>
public sealed class FirstPaneCommandWiringTests
{
    private static ShellSource App => ShellSource.Load("App.xaml.cs");

    // ---- cold start --------------------------------------------------------

    [Fact]
    public void ColdStart_ReadsTheLaunchCommandFromItsOwnArgv()
    {
        var launched = App.Method("OnLaunched");
        var parse = launched.Call("Ghostty.Core.SingleInstance.LaunchCommand.FromArgs");
        Assert.Equal("Environment.GetCommandLineArgs()", parse.Arg(0));
    }

    [Fact]
    public void ColdStart_DashE_SkipsSessionRestore()
    {
        // `wt -- cmd` opens the command; it does not bring back the layout.
        var launched = App.Method("OnLaunched");
        var restore = launched.Call("_sessionManager.LoadForRestore");
        var conditional = restore.Ancestors().OfType<ConditionalExpressionSyntax>().First();
        Assert.Contains("coldCommand is not null", conditional.Condition.ToString());
        Assert.Contains("honorJumpList", conditional.Condition.ToString());
    }

    [Fact]
    public void ColdStart_DashE_LeavesOnlyItsOwnWindowOutOfTheSession()
    {
        // The one-off window must not replace the layout the next plain
        // launch restores. The PROCESS keeps saving: in single-instance mode
        // it is the primary every later launch is forwarded to, and those
        // windows are the user's real work.
        var launched = App.Method("OnLaunched");
        var note = launched.Call("_sessionManager.NoteRestoreSkipped");
        var guard = note.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal("coldCommand is not null", guard.Condition.ToString());

        var member = launched.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "window.ExcludedFromSession");
        Assert.Equal("coldCommand is not null", member.Right.ToString());

        // Nothing switches persistence off for the whole process any more.
        var manager = ShellSource.Load("Session.SessionManager.cs");
        Assert.DoesNotContain("SuspendPersistence", manager.Root.ToString());
        Assert.DoesNotContain("_restoreSkipped", manager.Method("RequestPersist").Body!.ToString());
    }

    [Fact]
    public void ColdStart_FreshWindow_OpensOnTheLaunchsFirstPane_InTheCallersDirectory()
    {
        var launched = App.Method("OnLaunched");
        var fresh = launched.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == "MainWindow"
                && o.ArgumentList!.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "showLaunchIcon"
                    && a.Expression.ToString() == "true"))
            .ToList();
        Assert.Single(fresh);

        var seed = fresh[0].ArgumentList!.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == "initialSnapshot");
        var call = seed.Expression.AssertCallTo("LaunchFirstPaneSnapshot");
        Assert.Equal("coldCommand", call.Arg(0));
        Assert.Equal(
            "workingDirectory: coldCommand is null ? null : Program.LaunchWorkingDirectory",
            call.Arg(1));
    }

    // ---- forwarded launch and the jump list --------------------------------

    [Fact]
    public void ForwardedBareLaunch_HandsTheCommandOver()
    {
        var open = App.Method("OpenWindowFromLaunch");
        var call = open.Call("OpenJumpListWindow");
        Assert.Contains("LaunchCommand.FromArgs(req.Args)", call.Arg(2));
    }

    [Fact]
    public void JumpListWindow_NoProfileNamed_IsALaunchsFirstPane()
    {
        var builder = App.Method("OpenJumpListWindow");
        var helper = builder.Call("LaunchFirstPaneSnapshot");
        var branch = helper.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal("profileId is null", branch.Condition.ToString());
        Assert.Equal("command", helper.Arg(0));
        Assert.Equal("workingDirectory", helper.Arg(1));
    }

    [Fact]
    public void JumpListWindow_PickedProfile_RunsThatProfile()
    {
        // The profile the user picked, resolved by its own id; only -e on the
        // same launch may replace its command, never `command`.
        var builder = App.Method("OpenJumpListWindow");
        Assert.Contains("registry?.Resolve(profileId)", builder.Body!.ToString());
        var apply = builder.Call("Ghostty.Core.Profiles.PaneCommandPolicy.ApplyLaunchCommand");
        Assert.Equal("command", apply.Arg(1));
        Assert.DoesNotContain("ConfiguredCommand", builder.Body!.ToString());
    }

    [Fact]
    public void JumpListLaunch_PassesNoLaunchCommand()
    {
        // A jump-list entry never carries -e; the call must not grow one by
        // accident and make a picked profile run something else.
        var handler = App.Method("HandleJumpListLaunch");
        var call = handler.Call("OpenJumpListWindow");
        Assert.Equal(2, call.ArgumentList.Arguments.Count);
    }

    // ---- every other default pane ------------------------------------------

    [Fact]
    public void ImplicitDefault_ReadsTheConfiguredCommand_AndTheDefaultProfileFlag()
    {
        var helper = App.Method("ImplicitDefaultSnapshot");
        var call = helper.Call("Ghostty.Core.Profiles.PaneCommandPolicy.ImplicitDefault");
        Assert.Contains("ResolveDefault(ProfileRegistry)", call.Arg(0));
        Assert.Equal("ConfigService?.ConfiguredCommand", call.Arg(1));
        Assert.Equal("ConfigService?.DefaultProfileSet ?? false", call.Arg(2));
    }

    [Fact]
    public void MainWindow_FirstTab_AndSoTheQuickTerminal_UseTheImplicitDefault()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");
        var text = window.Root.ToString();
        Assert.Contains("initialSnapshot ?? App.ImplicitDefaultSnapshot()", text);
    }

    [Fact]
    public void NewTabChord_AndTheButtonsMainClick_OpenTheImplicitDefault()
    {
        var router = ShellSource.Load("Input.PaneActionRouter.cs");
        var chord = router.Method("OpenDefaultProfileTab");
        chord.Call("_openDefaultProfile");

        var window = ShellSource.Load("MainWindow.xaml.cs");
        var opener = window.Method("OpenDefaultProfile");
        opener.Call("App.ImplicitDefaultSnapshot");
        Assert.Contains("openDefaultProfile: OpenDefaultProfile", window.Root.ToString());

        var button = ShellSource.Load("Tabs.NewTabSplitButton.xaml.cs");
        button.Method("OnPrimaryClick").Call("Owner.OpenDefaultProfile");
        // The rows are the profile picks.
        button.Method("OnRowClick").Call("Owner.OpenProfile");

        // The tab strip's own New Tab item.
        var strip = ShellSource.Load("Tabs.StripContextMenuBuilder.cs");
        Assert.Single(strip.Root.Calls("manager.NewTab"),
            c => c.Arg(0) == "App.ImplicitDefaultSnapshot()");
    }

    [Fact]
    public void JumpListNewTab_NoProfileNamed_OpensTheImplicitDefault()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");
        var tab = window.Method("OpenJumpListTab");
        var call = tab.Call("OpenDefaultProfile");
        var guard = call.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal("profileId is null", guard.Condition.ToString());
    }

    [Fact]
    public void Split_DoesNotInheritALaunchCommand()
    {
        var host = ShellSource.Load("Panes.PaneHost.cs");
        var split = host.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Split" && m.ParameterList.Parameters.Count == 1);
        var inherit = split.Call("Ghostty.Core.Profiles.PaneCommandPolicy.Inherit");
        Assert.Equal("_activeLeaf.Snapshot", inherit.Arg(0));
        Assert.Contains("App.ImplicitDefaultSnapshot()", inherit.Arg(1));
    }

    // ---- the surface and the config ----------------------------------------

    [Fact]
    public void Surface_GetsAnArgvAsOne()
    {
        var control = ShellSource.Load("Controls.TerminalControl.xaml.cs");
        control.Method("OnLoaded").Call("Ghostty.Core.Profiles.PaneCommandPolicy.SurfaceCommand");
    }

    [Fact]
    public void ConfigService_ReadsTheCommandAndItsForm_AndFreesIt()
    {
        var config = ShellSource.Load("Services.ConfigService.cs");
        var flags = config.Method("ReadFlagsCore");
        Assert.Contains(flags.AssignsTo("ConfiguredCommand"),
            a => a.Right.ToString() == "ReadConfiguredCommand()");

        var read = config.Method("ReadConfiguredCommand");
        var call = read.Call("NativeMethods.ConfigCommand");
        Assert.Equal("out var direct", call.Arg(1));
        Assert.Contains("IsArgv: direct != 0", read.Body!.ToString());
        var free = read.Call("NativeMethods.StringFree");
        Assert.NotEmpty(free.Ancestors().OfType<FinallyClauseSyntax>());
    }

    [Fact]
    public void UI_DoesNotCallAProfileTheDefault_WhenTheCommandRuns()
    {
        var page = ShellSource.Load("Settings.Pages.ProfilesPage.xaml.cs");
        Assert.Contains("App.CommandInEffect", page.Root.ToString());

        var button = ShellSource.Load("Tabs.NewTabSplitButton.xaml.cs");
        var rebuild = button.Method("RebuildFlyout").Body!.ToString();
        Assert.Contains("var markDefault = App.CommandInEffect is null;", rebuild);
        Assert.Contains("markDefault && row.IsDefault", rebuild);
    }

    [Fact]
    public void Native_DropsInitialCommand_OnEverySurface()
    {
        // The host owns -e on Windows. If only surfaces given a host command
        // dropped initial-command, a surface without one (the hidden quick
        // terminal with no profile) initializing first would run -e too.
        var asm = typeof(FirstPaneCommandWiringTests).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.Replace('\\', '.').Replace('/', '.')
                .EndsWith("Interop.Exports.apprt.embedded.zig", System.StringComparison.Ordinal));
        using var reader = new System.IO.StreamReader(asm.GetManifestResourceStream(name)!);
        var text = reader.ReadToEnd().Replace("\r\n", "\n");

        const string drop = "config.@\"initial-command\" = null;";
        Assert.Equal(1, CountOf(text, drop));

        // Not inside the `if (opts.command)` block.
        var open = text.IndexOf("if (opts.command) |c_command| {", System.StringComparison.Ordinal);
        Assert.True(open >= 0);
        var depth = 0;
        var end = open;
        for (; end < text.Length; end++)
        {
            if (text[end] == '{') depth++;
            else if (text[end] == '}' && --depth == 0) break;
        }
        Assert.DoesNotContain(drop, text[open..end]);
        Assert.Contains("if (comptime builtin.os.tag == .windows) {\n            " + drop, text);
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            n++;
        return n;
    }

    [Fact]
    public void SessionManager_SkipsTheExcludedWindow_AndKeepsTheOthers()
    {
        var manager = ShellSource.Load("Session.SessionManager.cs");

        // The live capture skips only the excluded window, before capturing it.
        var persist = manager.Method("PersistLiveWindows");
        var loop = persist.DescendantNodes().OfType<ForEachStatementSyntax>().Single();
        var skip = loop.Statement.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Equal("w.ExcludedFromSession", skip.Condition.ToString());
        Assert.IsType<ContinueStatementSyntax>(skip.Statement);
        Assert.True(skip.SpanStart < persist.Call("w.CaptureSession").SpanStart);

        // A write is remembered, after it happens.
        var save = persist.Call("_store.Save");
        var wrote = persist.AssignsTo("_wroteThisRun").Single();
        Assert.True(wrote.SpanStart > save.SpanStart);
    }

    [Fact]
    public void SessionManager_CleanShutdown_LeavesAnUntouchedPreviousSessionAlone()
    {
        // A -e process that saved no window of its own must not mark the
        // previous session clean, nor replace it with the -e window.
        var manager = ShellSource.Load("Session.SessionManager.cs");
        var finalize = manager.Method("FinalizeCleanShutdown");
        var body = finalize.Body!;
        var guard = body.Statements.OfType<IfStatementSyntax>()
            .Single(s => s.Condition.ToString() == "_restoreSkipped && !_wroteThisRun");
        // It ends the method either way: the previous session is never
        // marked clean or replaced by the -e window from here.
        var block = Assert.IsType<BlockSyntax>(guard.Statement);
        Assert.IsType<ReturnStatementSyntax>(block.Statements.Last());
        Assert.True(guard.SpanStart < finalize.Call("_store.Load").SpanStart);

        // Only a real window is saved from inside it, and the -e window is
        // dropped from the fallback before anything reads it.
        var drop = body.Statements.OfType<IfStatementSyntax>()
            .Single(s => s.Condition.ToString() == "closingFallback is { ExcludedFromSession: true }");
        Assert.True(drop.SpanStart < guard.SpanStart);
        Assert.Contains("closingFallback?.CaptureSession()", block.ToString());
    }
}
