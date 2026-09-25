using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The shell side of #1136: every opener of a pane nobody picked a profile
/// for goes through <c>PaneCommandPolicy</c>, a cold <c>-e</c> opens only its
/// window and holds the saved session untouched until a plain launch
/// restores it, and an argv reaches the surface as one.
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
    public void ColdStart_DashE_DoesNotRestore()
    {
        var launched = App.Method("OnLaunched");
        var restore = launched.Call("_sessionManager.LoadForRestore");
        var conditional = restore.Ancestors().OfType<ConditionalExpressionSyntax>().First();
        Assert.Equal("honorJumpList || coldCommand is not null", conditional.Condition.ToString());
    }

    [Fact]
    public void ColdStart_DashE_HoldsTheSavedSession_BeforeAnyWindowOpens()
    {
        var launched = App.Method("OnLaunched");
        var hold = launched.Call("_sessionManager.HoldForLaunchCommand");
        var guard = hold.Ancestors().OfType<IfStatementSyntax>().First();
        // Only when -e is what opens; a jump-list click wins and is saved.
        Assert.Equal("coldCommand is not null && !honorJumpList", guard.Condition.ToString());
        Assert.True(hold.SpanStart < FreshWindow(launched).SpanStart);
    }

    [Fact]
    public void ColdStart_OpensOnlyOneKindOfWindow()
    {
        // Restored windows, a jump-list window, or the fresh (-e, initial-
        // command or default) window: one of the three, never -e beside a
        // restore.
        var launched = App.Method("OnLaunched");
        var fresh = FreshWindow(launched);
        var chain = fresh.Ancestors().OfType<IfStatementSyntax>().Last(
            i => i.Condition.ToString() == "restoreState is { Windows.Count: > 0 }");
        Assert.Contains("OpenRestoredWindows(restoreState", chain.Statement.ToString());
        var elseIf = Assert.IsType<IfStatementSyntax>(chain.Else!.Statement);
        Assert.Equal("honorJumpList", elseIf.Condition.ToString());
        Assert.Contains(fresh.ToString(), elseIf.Else!.Statement.ToString());
        Assert.Contains("_sessionManager.Track(window)", elseIf.Else.Statement.ToString());
    }

    [Fact]
    public void ForwardedPlainLaunch_RestoresTheHeldSession()
    {
        // A plain launch restores the held session INSTEAD of a default
        // window, as a plain cold launch would have.
        var open = App.Method("OpenWindowFromLaunch");
        var attempt = open.Call("RestoreHeldSession");
        var guard = attempt.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal(
            "forwardedCommand is null && launch.ProfileId is null && RestoreHeldSession()",
            guard.Condition.ToString());
        Assert.IsType<ReturnStatementSyntax>(guard.Statement);
        Assert.True(guard.SpanStart < open.Call("OpenJumpListWindow").SpanStart);

        var restore = App.Method("RestoreHeldSession");
        restore.Call("manager.ReleaseHeldSession");
        var reopen = restore.Call("OpenRestoredWindows");
        Assert.Equal("showLaunchIconOnFirst: false", reopen.Arg(1));
        restore.Call("manager.RequestPersist");
    }

    // Every place the shell creates a regular window, and the jump list's
    // New Tab, ends a cold -e's hold before it opens anything: the held
    // session is restored first, then the new window opens (#1136). A census
    // over the whole shell, so a new opener that forgets it fails here.
    [Fact]
    public void EveryWindowOpener_EndsTheHold_BeforeItCreatesTheWindow()
    {
        // Where a window is created WITHOUT ending the hold, on purpose:
        // the cold start itself (it sets the hold), the quick terminal (not
        // a regular window), the restore (it is the release), and the two
        // factories (their callers are checked instead).
        var exempt = new[] { "OnLaunched", "OpenRestoredWindows", "CreateForAdoption", "CreateForNewTab" };
        var releases = new[] { "RestoreHeldSession", "App.RestoreHeldSessionBeforeNewWindow" };

        var sites = 0;
        var checkedSites = 0;
        var problems = new System.Collections.Generic.List<string>();
        foreach (var file in ShellSource.AllFiles())
        {
            var creations = file.Root.DescendantNodes()
                .Where(n => n is ObjectCreationExpressionSyntax o && o.Type.ToString() == "MainWindow"
                    || n is InvocationExpressionSyntax i && i.CalleeText() is
                        "MainWindow.CreateForNewTab" or "CreateForNewTab"
                        or "MainWindow.CreateForAdoption" or "CreateForAdoption");
            foreach (var creation in creations)
            {
                sites++;
                var method = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var name = method?.Identifier.ValueText ?? "(no method)";
                if (exempt.Contains(name)) continue;
                checkedSites++;
                var release = method!.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(i => releases.Contains(i.CalleeText()));
                if (release is null)
                    problems.Add($"  {file.Name}: {name} opens a window without ending the hold");
                else if (release.SpanStart > creation.SpanStart)
                    problems.Add($"  {file.Name}: {name} ends the hold after it opens the window");
            }
        }

        // Non-vacuity: the census found the openers it is about.
        // Today: 9 creations, 4 checked openers (OpenJumpListWindow,
        // ReopenClosedWindow, OpenInNewWindow, DetachTabToWindow).
        Assert.True(sites >= 9, $"found only {sites} window creations; the census stopped matching");
        Assert.True(checkedSites >= 4, $"checked only {checkedSites} openers");
        Assert.True(problems.Count == 0, "window openers that skip the hold:\n" + string.Join("\n", problems));

        // The jump list's New Tab opens no window but is a launch all the same.
        var tab = App.Method("TryOpenJumpListTab");
        Assert.IsType<ExpressionStatementSyntax>(tab.Body!.Statements[0]);
        Assert.Equal("RestoreHeldSession()", ((ExpressionStatementSyntax)tab.Body.Statements[0]).Expression.ToString());
    }

    [Fact]
    public void SessionManager_HoldWritesNothing_UntilReleased()
    {
        var manager = ShellSource.Load("Session.SessionManager.cs");
        foreach (var name in new[] { "RequestPersist", "PersistLiveWindows", "FinalizeCleanShutdown" })
        {
            var body = manager.Method(name).Body!;
            var guard = body.Statements.OfType<IfStatementSyntax>()
                .FirstOrDefault(s => s.Condition.ToString() == "_held" && s.Statement is ReturnStatementSyntax);
            Assert.True(guard is not null, $"{name} has no `if (_held) return;`");
            var firstEffect = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.ToString().StartsWith("_store.") || i.ToString().StartsWith("_debounce.Start"))
                .Select(i => i.SpanStart)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            Assert.True(guard!.SpanStart < firstEffect, $"{name} writes before the hold check");
        }

        // The release ends the hold and answers a cold launch's restore.
        var release = manager.Method("ReleaseHeldSession").Body!.ToString();
        Assert.Contains("_held = false;", release);
        Assert.Contains("return LoadForRestore();", release);
    }

    [Fact]
    public void ColdStart_InitialCommand_IsTheLaunchCommand_AndDashEWins()
    {
        var launched = App.Method("OnLaunched");
        var initial = launched.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "initialCommand");
        Assert.Equal(
            "coldCommand is null ? _configService.ConfiguredInitialCommand : null",
            initial.Initializer!.Value.ToString());
    }

    private static ObjectCreationExpressionSyntax FreshWindow(MethodDeclarationSyntax launched)
        => launched.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "MainWindow"
                && o.ArgumentList!.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "initialSnapshot"));

    [Fact]
    public void ColdStart_FreshWindow_OpensOnTheLaunchsFirstPane_InTheCallersDirectory()
    {
        var launched = App.Method("OnLaunched");
        var fresh = FreshWindow(launched);

        var seed = fresh.ArgumentList!.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == "initialSnapshot");
        var call = seed.Expression.AssertCallTo("LaunchFirstPaneSnapshot");
        Assert.Equal("coldCommand", call.Arg(0));
        Assert.Equal(
            "workingDirectory: coldCommand is null ? null : Program.LaunchWorkingDirectory",
            call.Arg(1));
        Assert.Equal("initialCommand: initialCommand", call.Arg(2));

        // It is the only window of the launch, so it takes the splash.
        var icon = fresh.ArgumentList.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == "showLaunchIcon");
        Assert.Equal("true", icon.Expression.ToString());
    }

    // ---- forwarded launch and the jump list --------------------------------

    [Fact]
    public void ForwardedBareLaunch_HandsTheCommandOver()
    {
        var open = App.Method("OpenWindowFromLaunch");
        var call = open.Call("OpenJumpListWindow");
        Assert.Equal("command: forwardedCommand", call.Arg(2));
        var parsed = open.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "forwardedCommand");
        Assert.Contains("LaunchCommand.FromArgs(req.Args)", parsed.Initializer!.Value.ToString());
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
        // The surface is created from the first layout pass, not from
        // OnLoaded itself (a hidden tab has no size until it is shown).
        control.Method("TryCreateSurface").Call("Ghostty.Core.Profiles.PaneCommandPolicy.SurfaceCommand");
    }

    [Fact]
    public void ConfigService_ReadsTheCommandAndItsForm_AndFreesIt()
    {
        var config = ShellSource.Load("Services.ConfigService.cs");
        var flags = config.Method("ReadFlagsCore");
        Assert.Contains(flags.AssignsTo("ConfiguredCommand"),
            a => a.Right.ToString() == "ReadConfiguredCommand()");
        Assert.Contains(flags.AssignsTo("ConfiguredInitialCommand"),
            a => a.Right.ToString() == "ReadConfiguredInitialCommand()");

        var command = config.Method("ReadConfiguredCommand").Call("NativeMethods.ConfigCommand");
        Assert.Equal("out var direct", command.Arg(1));
        var initial = config.Method("ReadConfiguredInitialCommand").Call("NativeMethods.ConfigInitialCommand");
        Assert.Equal("out var direct", initial.Arg(1));

        var read = config.Method("ReadCommand");
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

    [Fact]
    public void LaunchPane_ClosesOnCleanExit_EndToEndWiring()
    {
        // Host: the surface config carries the policy's answer. The surface
        // is created from the first layout pass, not from OnLoaded itself
        // (a hidden tab has no size until it is shown).
        var control = ShellSource.Load("Controls.TerminalControl.xaml.cs");
        var create = control.Method("TryCreateSurface").Body!.ToString();
        Assert.Contains("surfaceConfig.CloseOnCleanExit =", create);
        Assert.Contains("PaneCommandPolicy.ClosesOnCleanExit(Snapshot)", create);

        // Struct layout: held to the header by computed offsets in
        // GhosttyStructHeaderParityTests (SurfaceConfig_*), not here.

        // Native: honoured only when the user did not set wait-after-command,
        // and set before any child exit can be processed.
        var text = EmbeddedZig();
        Assert.Contains("close_on_clean_exit: bool = false,", text);
        Assert.Contains(
            "self.core_surface.close_on_clean_exit =\n            opts.close_on_clean_exit and !user_wait_after_command;",
            text);
        Assert.True(
            text.IndexOf("const user_wait_after_command = config.@\"wait-after-command\";", System.StringComparison.Ordinal)
            < text.IndexOf("if (opts.command) |c_command| {", System.StringComparison.Ordinal),
            "the user's wait-after-command must be read before a host command forces it on");
    }

    private static string EmbeddedZig()
    {
        var asm = typeof(FirstPaneCommandWiringTests).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.Replace('\\', '.').Replace('/', '.')
                .EndsWith("Interop.Exports.apprt.embedded.zig", System.StringComparison.Ordinal));
        using var reader = new System.IO.StreamReader(asm.GetManifestResourceStream(name)!);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            n++;
        return n;
    }
}
