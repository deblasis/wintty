using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Wiring guards for the Animations setting: the app installs the user
/// lever's config source once, the in-app support dump carries the
/// resolved state at open AND re-resolves it at copy, and the CLI dump
/// does not invent one. Wiring, not behaviour: the level arithmetic lives
/// in Motion.MotionGatingLeverTests, the renderer's emission in
/// Version.VersionRendererAnimationsTests, and the state read's freshness
/// (the thing copy time depends on) in the Windows suite's
/// AnimationsStateTests, which runs the same source the app compiles.
/// </summary>
public class AnimationsWiringTests
{
    [Fact]
    public void The_app_installs_the_lever_source_from_the_animations_key()
    {
        var app = ShellSource.Load("App.xaml.cs");

        var install = Assert.Single(app.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("SetUserLeverSource", System.StringComparison.Ordinal)));

        // The thunk reads the key fresh per ask, the way the power
        // monitor's readMode does, and parses through the vocabulary
        // helper rather than spelling the values a second time.
        var read = Assert.Single(install.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("GetRawFileValue", System.StringComparison.Ordinal)));
        Assert.Equal("\"animations\"", read.Arg(0));

        Assert.Single(install.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("UserMotionLeverValues.Parse", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void The_support_dump_carries_the_resolved_state()
    {
        var dialog = ShellSource.Load("Dialogs.VersionDialog.xaml.cs");

        var build = Assert.Single(dialog.Root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == "VersionRenderer.Build"));

        // The state is passed, not defaulted away: this clipboard payload
        // is the bug-report use case the field exists for.
        Assert.True(build.ArgumentList.Arguments.Count == 1,
            $"VersionRenderer.Build must be called with the resolved state, " +
            $"found {build.ArgumentList.Arguments.Count} arguments");
        build.ArgumentList.Arguments[0].Expression.AssertCallTo("AnimationsState.Code");
    }

    [Fact]
    public void The_copy_handler_re_resolves_the_state_at_copy_time()
    {
        var dialog = ShellSource.Load("Dialogs.VersionDialog.xaml.cs");

        // The paste asks fresh, inside the handler: the resolved state can
        // change while the dialog sits open (the Animations lever in another
        // window, battery saver), and the dump is the bug-report use case
        // that must state what holds now. A stored payload field is the
        // freeze this replaces, so its absence is pinned too: a copy that
        // reads a field instead of the seam has nowhere to read from.
        var onCopy = dialog.Method("OnCopy");
        Assert.Single(onCopy.Calls("Ghostty.Services.AnimationsState.ComposeDump"));

        // The clipboard gets the fresh payload...
        var setText = Assert.Single(onCopy.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith(".SetText", System.StringComparison.Ordinal)));
        Assert.Equal("dump.Payload", setText.Arg(0));

        // ...and the display body refreshes from the same ask, so the
        // dialog on screen and the text on the clipboard cannot disagree.
        var refresh = Assert.Single(onCopy.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "VersionText.Text"));
        Assert.Equal("dump.Body", refresh.Right.ToString());

        Assert.DoesNotContain(
            dialog.Root.DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables)
                .Select(v => v.Identifier.ValueText),
            name => name == "_output");
    }

    [Fact]
    public void The_payload_seam_passes_the_resolved_state_to_the_renderer()
    {
        // The payload is composed in one place, and the state reaches the
        // renderer there: a Build the refactor left argument-less would drop
        // the animations line from every paste while every other guard
        // stays green.
        var compose = ShellSource.Load("Services.AnimationsState.cs").Method("ComposeDump");

        var build = compose.Call("VersionRenderer.Build");
        Assert.True(build.ArgumentList.Arguments.Count == 1,
            $"the dump's Build must be called with the resolved state, " +
            $"found {build.ArgumentList.Arguments.Count} arguments");
        build.ArgumentList.Arguments[0].Expression.AssertCallTo("Code");

        // One ask, both renders: the full clipboard payload (header + URL
        // line + body) and the body the dialog displays.
        Assert.Single(compose.Calls("VersionRenderer.RenderPlain"));
        Assert.Single(compose.Calls("VersionRenderer.RenderPlainBody"));
    }

    [Fact]
    public void The_cli_version_dump_does_not_invent_a_state()
    {
        var cli = ShellSource.Load("Cli.CliActions.cs");

        var build = Assert.Single(cli.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "VersionRenderer.Build"));

        // +version runs before any config exists, by design. A state it
        // cannot resolve must be absent from the dump, not guessed.
        Assert.True(build.ArgumentList.Arguments.Count == 0,
            "the CLI must not pass a resolved state it has no config to read");
    }

    [Fact]
    public void The_dump_vocabulary_comes_off_the_gate()
    {
        var state = ShellSource.Load("Services.AnimationsState.cs");

        // One gate read, on the ambient floor, with the same OS read the
        // gates use: the field reports what the app is actually running.
        Assert.Single(state.Method("Code").Calls("MotionGating.Effective"));
        Assert.Single(state.Method("Code").Calls("SystemAnimations.ReadAnimationsEnabled"));

        // Neutral codes, public vocabulary.
        var code = state.Method("Code").ToString();
        Assert.Contains("\"full\"", code, System.StringComparison.Ordinal);
        Assert.Contains("\"reduced\"", code, System.StringComparison.Ordinal);
        Assert.Contains("\"off\"", code, System.StringComparison.Ordinal);
    }
}
