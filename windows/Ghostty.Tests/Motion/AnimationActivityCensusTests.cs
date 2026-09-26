using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The animation-start census.
///
/// Every animation start in the shell -- a Storyboard Begin, a composition
/// StartAnimation, a markup theme transition, a VisualStateManager.GoToState
/// that asks for transitions -- must route through AnimationActivityRegistry,
/// the one place that can answer "is anything animating on this element and
/// property right now". This census is what keeps that answer honest: it
/// sweeps every shell source, and a new animation start that appears without
/// a registry call beside it fails here with its file and line.
///
/// Routing wraps the start: the helper performs the Begin/StartAnimation
/// itself, so a routed site stands in the source as a registry call. The
/// sweep therefore reads two shapes -- raw starts (which must have a
/// registry call within <see cref="RoutingProximityLines"/> lines) and the
/// registry calls themselves, whose per-file counts the inventory pins.
///
/// Swept scope: ALL of windows/Ghostty/ (.cs via the embedded shell sources,
/// .xaml via the embedded XamlCensus resources). Declared exclusions, by
/// name, and why each one may sit outside the registry:
///
/// - Settings/        out of scope for this work
/// - Demo/            dev-only, never ships
/// - Testing/         harness code
/// - Motion/          the seam's own folder; it starts no animations, and
///                    excluding it keeps this tripwire one-directional
/// - Shell.GradientTintVisual.cs  the ambient background tint, a declared
///                    rider whose six composition starts predate the registry
/// - Services.AnimationActivityRegistry.cs  the router itself: the one
///                    file in the shell allowed to call Begin directly
///
/// The canonical false positive this census must never match is
/// `_themeMode.Browse.Begin()` in Commands/CommandPaletteViewModel.cs: a
/// theme-preview session's Begin, not a Storyboard's. The scanner classifies
/// a .Begin by its receiver's declared type, so a Begin on anything that is
/// not a Storyboard is not an animation start, and this file pins that.
/// </summary>
public class AnimationActivityCensusTests
{
    private const string ShellPrefix = "Ghostty.Tests.Interop.Sources.Ghostty.";
    private const string CorePrefix = "Ghostty.Tests.Interop.Sources.Ghostty.Core.";
    private const string XamlPrefix = "Ghostty.Tests.XamlCensus.";
    private const string RegistryCall = "AnimationActivityRegistry.";
    private const int RoutingProximityLines = 14;

    private const string KindStoryboardBegin = "storyboard-begin";
    private const string KindStartAnimation = "start-animation";
    private const string KindGoToStateTrue = "goto-state-with-transitions";
    private const string KindXamlTransition = "xaml-theme-transition";
    private const string KindRegistryStart = "registry-start";

    private readonly record struct Finding(
        string Tail, int Line, string Kind, string Detail, bool Routed);

    // -- The declared exclusions ------------------------------------------

    private static readonly string[] ExcludedTailPrefixes =
    {
        "Settings.", "Demo.", "Testing.", "Motion.",
    };

    private static bool Excluded(string tail) =>
        ExcludedTailPrefixes.Any(p => tail.StartsWith(p, StringComparison.Ordinal))
        || tail == "Shell.GradientTintVisual.cs"
        || tail == "Services.AnimationActivityRegistry.cs";

    // -- The inventory (the non-vacuity pin) ------------------------------
    //
    // Routing WRAPS the start: a routed site stands in the source as an
    // AnimationActivityRegistry.BeginStoryboard / .StartCompositionAnimation
    // call, so the inventory pins those calls per file, with the exact count
    // the sweep must find. A row that stops matching means the file's starts
    // changed: either an animation was removed (fix the row) or a new one
    // joined it (route it, then fix the row). Adding a start without routing
    // trips the unrouted-start sweep instead; either way this census reds.

    private static readonly (string Tail, string Kind, int Count)[] Inventory =
    {
        ("Controls.TerminalControl.xaml.cs", KindRegistryStart, 1),   // bell fade storyboard
        ("Tabs.ActiveFieldFill.cs", KindRegistryStart, 1),            // field colour settle (brush target)
        ("Tabs.TabRunLabel.cs", KindRegistryStart, 1),                // run label fade
        ("Tabs.TabSwitcherPopup.xaml.cs", KindRegistryStart, 3),      // highlight move (card or board key) + enter rise
        ("Tabs.VerticalTabStrip.xaml.cs", KindRegistryStart, 13),     // 2 field boards + follow/co-drag/glides/lift/flights/settle
        ("Tabs.TabHost.xaml.cs", KindRegistryStart, 5),               // lift, shadow in/out, settle, appear fade
        ("Tabs.TabJoinRing.cs", KindRegistryStart, 1),                // join ring spring
        ("Tabs.TabPinBandPanel.cs", KindRegistryStart, 1),            // square glide
        ("Shell.LayoutSwitchTimeline.cs", KindRegistryStart, 5),      // Register's two branches, the pivot, the T and S drivers
        ("Panes.PaneStartupGlow.cs", KindRegistryStart, 3),           // orbit on two brushes + the fade; the orbit is Forever
        ("Tabs.TabOverviewControl.xaml", KindXamlTransition, 2),      // EntranceThemeTransition + ContentThemeTransition
    };

    // -- The sweep ---------------------------------------------------------

    private static string Normalize(string resourceName) =>
        resourceName.Replace('\\', '.').Replace('/', '.');

    private static List<(string Tail, string Text)> ShellTexts()
    {
        var asm = Assembly.GetExecutingAssembly();
        var found = new List<(string, string)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var dotted = Normalize(name);
            if (!dotted.StartsWith(ShellPrefix, StringComparison.Ordinal)) continue;
            if (dotted.StartsWith(CorePrefix, StringComparison.Ordinal)) continue;
            if (!dotted.EndsWith(".cs", StringComparison.Ordinal)) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new System.IO.StreamReader(stream);
            found.Add((dotted[ShellPrefix.Length..], reader.ReadToEnd()));
        }

        Assert.True(found.Count > 0, "no embedded shell sources found under " + ShellPrefix);
        return found;
    }

    private static List<(string Tail, string Text)> XamlTexts()
    {
        var asm = Assembly.GetExecutingAssembly();
        var found = new List<(string, string)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var dotted = Normalize(name);
            if (!dotted.StartsWith(XamlPrefix, StringComparison.Ordinal)) continue;
            if (!dotted.EndsWith(".xaml", StringComparison.Ordinal)) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new System.IO.StreamReader(stream);
            found.Add((dotted[XamlPrefix.Length..], reader.ReadToEnd()));
        }

        Assert.True(found.Count > 0, "no embedded shell XAML found under " + XamlPrefix);
        return found;
    }

    /// <summary>
    /// The scanner, as a pure function of (tail, text) so the mutation pins
    /// below can exercise it on synthetic sources without the corpus.
    /// </summary>
    private static List<Finding> ScanCSharp(string tail, string text)
    {
        var root = Ghostty.Tests.Wiring.ShellSource.ParseForCorpusScan(text).Root;

        // name -> every type text its declarations and initializers offer.
        var declared = DeclaredTypes(root);

        var findings = new List<Finding>();
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var callee = CalleeText(call);
            var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            if (callee.EndsWith(".Begin", StringComparison.Ordinal)
                && IsStoryboardReceiver(call, declared))
            {
                findings.Add(Make(tail, text, line, KindStoryboardBegin, callee));
            }
            else if (callee.EndsWith(".StartAnimation", StringComparison.Ordinal))
            {
                findings.Add(Make(tail, text, line, KindStartAnimation, callee));
            }
            else if (callee.EndsWith(".BeginStoryboard", StringComparison.Ordinal)
                     || callee.EndsWith(".StartCompositionAnimation", StringComparison.Ordinal))
            {
                // A routed start: the registry call performs the Begin or the
                // StartAnimation itself, so the call site IS the animation
                // start. Routed by construction.
                findings.Add(new Finding(tail, line, KindRegistryStart, callee, Routed: true));
            }
            else if (callee.EndsWith(".GoToState", StringComparison.Ordinal)
                     && call.ArgumentList.Arguments.Count > 0
                     && call.ArgumentList.Arguments[^1].Expression
                         is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax literal
                     && literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.TrueLiteralExpression))
            {
                findings.Add(Make(tail, text, line, KindGoToStateTrue, callee));
            }
        }

        return findings;
    }

    private static List<Finding> ScanXaml(string tail, string text)
    {
        var findings = new List<Finding>();
        foreach (var match in Regex.Matches(
            text, @"<\w*ThemeTransition\b").Cast<Match>())
        {
            var line = text[..match.Index].Count(c => c == '\n') + 1;
            findings.Add(new Finding(tail, line, KindXamlTransition, match.Value, Routed: true));
        }

        return findings;
    }

    private static Finding Make(
        string tail, string text, int line, string kind, string detail)
    {
        var lines = text.Split('\n');
        var from = Math.Max(0, line - 1 - RoutingProximityLines);
        var to = Math.Min(lines.Length - 1, line - 1 + RoutingProximityLines);
        for (var i = from; i <= to; i++)
        {
            if (lines[i].Contains(RegistryCall, StringComparison.Ordinal))
            {
                return new Finding(tail, line, kind, detail, Routed: true);
            }
        }

        return new Finding(tail, line, kind, detail, Routed: false);
    }

    // The callee the way the source spells it, receiver included, matching
    // the established convention in ShellSource.
    private static string CalleeText(InvocationExpressionSyntax call)
    {
        if (call.Expression is MemberBindingExpressionSyntax
            && call.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()
                is { } conditional)
        {
            return conditional.Expression + "?" + call.Expression;
        }

        return call.Expression.ToString();
    }

    /// <summary>
    /// Every name declared in the file, mapped to the type texts its
    /// declarations offer: the declared type, plus an object-creation
    /// initializer's type for the `var board = new Anim.Storyboard()` shape.
    /// A .Begin whose receiver's root name never declares a Storyboard type
    /// is not a storyboard begin -- that is what keeps the theme-preview
    /// session's `_themeMode.Browse.Begin()` and the drag state machine's
    /// `drag.Machine.Begin()` out of the census.
    /// </summary>
    private static Dictionary<string, List<string>> DeclaredTypes(SyntaxNode root)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var variables = root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>();
        foreach (var declaration in variables)
        {
            var typeText = declaration.Type.ToString();
            var creationType = declaration.Variables
                .Select(v => v.Initializer?.Value)
                .OfType<ObjectCreationExpressionSyntax>()
                .Select(o => o.Type.ToString())
                .ToList();
            foreach (var variable in declaration.Variables)
            {
                if (!map.TryGetValue(variable.Identifier.ValueText, out var list))
                {
                    list = new List<string>();
                    map[variable.Identifier.ValueText] = list;
                }

                list.Add(typeText);
                list.AddRange(creationType);
            }
        }

        // A storyboard handed in as a parameter can be begun on too.
        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (!map.TryGetValue(parameter.Identifier.ValueText, out var list))
            {
                list = new List<string>();
                map[parameter.Identifier.ValueText] = list;
            }

            list.Add(parameter.Type?.ToString() ?? "");
        }

        return map;
    }

    private static bool IsStoryboardReceiver(
        InvocationExpressionSyntax call, Dictionary<string, List<string>> declared)
    {
        // The receiver as written: "_fade", "drag.Machine", "_themeMode.Browse".
        var receiver = call.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Expression.ToString(),
            _ => null,
        };
        if (receiver is null) return false;

        // The root name of the receiver, so "_themeMode.Browse" resolves
        // against declarations of _themeMode, not of Browse.
        var root = receiver.Split('.')[0].TrimEnd('!');
        return declared.TryGetValue(root, out var texts)
            && texts.Any(t => t.Contains("Storyboard", StringComparison.Ordinal));
    }

    private static List<Finding> SweepCSharp() =>
        ShellTexts()
            .Where(f => !Excluded(f.Tail))
            .SelectMany(f => ScanCSharp(f.Tail, f.Text))
            .ToList();

    private static List<Finding> SweepXaml() =>
        XamlTexts()
            .Where(f => !Excluded(f.Tail))
            .SelectMany(f => ScanXaml(f.Tail, f.Text))
            .ToList();

    // -- The tests ----------------------------------------------------------

    [Fact]
    public void EveryFoundStart_IsRouted_WithExactlyTheInventoryCounts()
    {
        var findings = SweepCSharp().Concat(SweepXaml()).ToList();

        var unrouted = findings.Where(f => !f.Routed).ToList();
        Assert.True(
            unrouted.Count == 0,
            "unrouted animation start(s) found:\n"
            + string.Join(
                "\n",
                unrouted.Select(f =>
                    $"  {f.Tail}:{f.Line} {f.Kind} `{f.Detail}` - route it through "
                    + $"{RegistryCall.TrimEnd('.')} and, if it is a new site, add it to this census's inventory")));

        var uncovered = findings
            .Where(f => !Inventory.Any(row => f.Tail == row.Tail && f.Kind == row.Kind))
            .ToList();
        Assert.True(
            uncovered.Count == 0,
            "animation start(s) outside every inventory row:\n"
            + string.Join(
                "\n",
                uncovered.Select(f => $"  {f.Tail}:{f.Line} {f.Kind} `{f.Detail}`"))
            + "\nThe inventory must match the tree: add (or drop) rows, never exemptions.");

        foreach (var (tail, kind, expected) in Inventory)
        {
            var actual = findings.Count(f => f.Tail == tail && f.Kind == kind);
            Assert.True(
                actual == expected,
                $"{tail}: expected {expected} {kind} site(s), found {actual}. "
                + "The inventory in AnimationActivityCensusTests must match the tree: "
                + "route every animation start through " + RegistryCall.TrimEnd('.')
                + ", then update the row.");
        }

        // Global non-vacuity: the inventory rows must account for every
        // finding; this catches an exclusion list that grew wide enough to
        // swallow routed sites.
        Assert.Equal(
            Inventory.Sum(row => row.Count),
            findings.Count);
    }

    [Fact]
    public void TheXamlChannel_IsTheOnlyOneThatSees_TabOverviewControl()
    {
        // The overview declares both of its transitions in markup and starts
        // nothing in code, so it is findable only through the XAML half of
        // the sweep. That asymmetry is the XAML channel's non-vacuity proof:
        // a sweep that lost the markup half would go silent on this file.
        Assert.Empty(ScanCSharp("Tabs.TabOverviewControl.xaml.cs", ShellText("Tabs.TabOverviewControl.xaml.cs")));
        var xaml = ScanXaml("Tabs.TabOverviewControl.xaml", ShellXaml("Tabs.TabOverviewControl.xaml"));
        Assert.Equal(2, xaml.Count);
        Assert.All(xaml, f => Assert.Equal(KindXamlTransition, f.Kind));
    }

    [Fact]
    public void ExcludedFiles_ExistWithAnimationStarts_AndAreFoundOnlyViaTheExclusionList()
    {
        // The exclusion entries must name files that genuinely carry
        // animation starts, so the list cannot rot into "excluding" a file
        // that no longer needs it. Each one is scanned directly, bypassing
        // the exclusion list.
        var tint = ScanCSharp("Shell.GradientTintVisual.cs", ShellText("Shell.GradientTintVisual.cs"));
        Assert.True(
            tint.Count(f => f.Kind == KindStartAnimation) >= 6,
            "Shell/GradientTintVisual.cs no longer carries its ambient tint starts; "
            + "drop it from the exclusion list.");

        var locator = ScanCSharp("Settings.SettingsCardLocator.cs", ShellText("Settings.SettingsCardLocator.cs"));
        Assert.Contains(locator, f => f.Kind == KindStartAnimation);

        var demo = ScanCSharp("Demo.DemoOverlay.xaml.cs", ShellText("Demo.DemoOverlay.xaml.cs"));
        Assert.Contains(demo, f => f.Kind == KindStoryboardBegin);

        // The registry's own file exists and really does Begin directly; the
        // exclusion must never quietly name a file that has gone away.
        var registry = ShellText("Services.AnimationActivityRegistry.cs");
        Assert.Contains("storyboard.Begin()", registry, StringComparison.Ordinal);

        // ...and the sweep itself must not report them.
        Assert.DoesNotContain(SweepCSharp(), f => f.Tail == "Shell.GradientTintVisual.cs");
        Assert.DoesNotContain(SweepCSharp(), f => f.Tail == "Services.AnimationActivityRegistry.cs");
        Assert.DoesNotContain(SweepCSharp(), f => f.Tail.StartsWith("Settings.", StringComparison.Ordinal));
        Assert.DoesNotContain(SweepCSharp(), f => f.Tail.StartsWith("Demo.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCanonicalFalsePositive_IsNeverMatched()
    {
        // _themeMode.Browse.Begin() is a theme-preview session beginning,
        // not a Storyboard. Its file must yield zero census findings.
        var palette = ScanCSharp(
            "Commands.CommandPaletteViewModel.cs", ShellText("Commands.CommandPaletteViewModel.cs"));
        Assert.True(
            palette.Count == 0,
            "the theme-preview Begin in Commands/CommandPaletteViewModel.cs must never match: "
            + string.Join(", ", palette.Select(f => $"{f.Line} {f.Kind}")));
    }

    [Fact]
    public void TheScanner_FindsAnUnroutedBegin_AndClassifiesFalsePositives()
    {
        const string routed = @"using System;
namespace Probe;
public sealed class Sample
{
    private Microsoft.UI.Xaml.Media.Animation.Storyboard _board = new();
    public void Go(Microsoft.UI.Xaml.UIElement el)
    {
        AnimationActivityRegistry.BeginStoryboard(_board, el, ""Opacity"");
        _board.Begin();
    }
}";
        var routedFindings = ScanCSharp("Probe.Sample.cs", routed);
        Assert.Equal(2, routedFindings.Count);
        // The wrapped form: the registry call itself is the start.
        Assert.Contains(routedFindings, f => f.Kind == KindRegistryStart && f.Routed);
        // The adjacency form: a raw Begin beside a registry call reads as routed.
        var raw = routedFindings.Single(f => f.Kind == KindStoryboardBegin);
        Assert.True(raw.Routed, "a Begin with a registry call within 14 lines must read as routed");

        const string unrouted = @"using System;
namespace Probe;
public sealed class Sample
{
    private Microsoft.UI.Xaml.Media.Animation.Storyboard _board = new();
    public void Go()
    {
        // far from any registry call, above and below
        _board.Begin();
    }
}";
        var unroutedFindings = ScanCSharp("Probe.Sample.cs", unrouted);
        var found = Assert.Single(unroutedFindings);
        Assert.False(found.Routed, "an unescorted Begin must read as unrouted");

        // The drag state machine's Begin and the theme-preview's Begin are
        // not Storyboards; a GoToState that asks for no transitions is not
        // a transition start either.
        const string negative = @"using System;
namespace Probe;
public sealed class Sample
{
    public void Go(DragMachine machine, ThemeMode themeMode, Control ctl)
    {
        if (!machine.Begin(5)) return;
        themeMode.Browse.Begin();
        VisualStateManager.GoToState(ctl, ""idle"", false);
    }
}";
        Assert.Empty(ScanCSharp("Probe.Sample.cs", negative));

        // The goto-with-transitions channel must be able to fire.
        const string gotoTrue = @"using System;
namespace Probe;
public sealed class Sample
{
    public void Go(Control ctl) => VisualStateManager.GoToState(ctl, ""wide"", true);
}";
        var gotoFinding = Assert.Single(ScanCSharp("Probe.Sample.cs", gotoTrue));
        Assert.Equal(KindGoToStateTrue, gotoFinding.Kind);
    }

    [Fact]
    public void TheScanner_FindsXamlThemeTransitions()
    {
        const string markup = @"<Grid
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
  <Border.Transitions>
    <TransitionCollection>
      <EntranceThemeTransition FromVerticalOffset=""24""/>
    </TransitionCollection>
  </Border.Transitions>
  <ContentControl.ContentTransitions>
    <TransitionCollection>
      <ContentThemeTransition/>
    </TransitionCollection>
  </ContentControl.ContentTransitions>
</Grid>";
        Assert.Equal(2, ScanXaml("Probe.Sample.xaml", markup).Count);
    }

    // -- Direct text access for the exclusion and false-positive pins -------

    private static string ShellText(string tail)
    {
        var match = Assert.Single(
            ShellTexts().Where(f => f.Tail == tail));
        return match.Text;
    }

    private static string ShellXaml(string tail)
    {
        var match = Assert.Single(
            XamlTexts().Where(f => f.Tail == tail));
        return match.Text;
    }
}
