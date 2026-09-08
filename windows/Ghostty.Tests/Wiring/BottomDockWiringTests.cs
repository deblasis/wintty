using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The notice surface gets a row of its own under the terminal. Sharing the
/// terminal's cell put a translucent InfoBar over the swapchain (report 3,
/// 2026-09-08); a reserved Auto row makes the terminal shrink instead.
/// </summary>
public sealed class BottomDockWiringTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument MainWindowXaml()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ghostty.Tests.MainWindow.xaml")!;
        return XDocument.Load(s);
    }

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void RootGrid_HasThreeRows_TerminalStar_DockAuto()
    {
        var root = Named(MainWindowXaml(), "RootGrid");
        var rows = root.Elements().Single(e => e.Name.LocalName == "Grid.RowDefinitions")
            .Elements().Select(r => (string?)r.Attribute("Height")).ToList();
        Assert.Equal(["Auto", "*", "Auto"], rows);
    }

    [Fact]
    public void PaneHostContainer_StaysInTheStarRow()
    {
        Assert.Equal("1", (string?)Named(MainWindowXaml(), "PaneHostContainer").Attribute("Grid.Row"));
    }

    [Fact]
    public void NotificationHost_OwnsTheDockRow_NotAnOverlay()
    {
        var host = Named(MainWindowXaml(), "NotificationHost");
        Assert.Equal("2", (string?)host.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)host.Attribute("Grid.Column"));
        Assert.Null(host.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void EveryRowSpanningOverlay_SpansAllThreeRows()
    {
        var spans = MainWindowXaml().Descendants()
            .Where(e => e.Attribute("Grid.RowSpan") is not null)
            .Select(e => ((string?)e.Attribute(X + "Name") ?? e.Name.LocalName, (string?)e.Attribute("Grid.RowSpan")))
            .ToList();
        Assert.NotEmpty(spans);
        Assert.All(spans, s => Assert.Equal("3", s.Item2));
    }

    [Fact]
    public void DockSizeChange_NotesALayoutSwitch_OnEveryTab()
    {
        var src = ShellSource.Load("MainWindow.xaml.cs");
        var handler = src.Method("OnNotificationDockSizeChanged");
        var calls = handler.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString()).ToList();
        // Every tab, the same reach NoteLayoutSwitchToSurfaces already uses
        // for an actual tab-layout switch: a background tab's PaneHost is
        // never laid out while hidden, so it would otherwise flash the
        // cols x rows pill the moment the user switches to it after a dock
        // resize, not at the moment the dock actually resized (fix round 1,
        // finding 6).
        Assert.Contains(calls, c => c.EndsWith("NoteLayoutSwitchToSurfaces"));
    }

    /// <summary>
    /// EveryRowSpanningOverlay_SpansAllThreeRows above reads MainWindow.xaml
    /// and structurally cannot see anything assigned in code-behind. Two
    /// RootGrid children, _verticalTabHost and _verticalSeamCover, get their
    /// span from Grid.SetRowSpan in MainWindow.xaml.cs instead, and both were
    /// missed by the XAML-only sweep when the dock row was added (fix round
    /// 1, finding 3). This reads the code-behind directly so that class of
    /// bug cannot recur.
    ///
    /// Scoped to MainWindow.xaml.cs, where RootGrid's own children are added:
    /// a RowSpan set on a grid a control builds for ITSELF -- e.g.
    /// TabSwitcherPopup's per-cell field/end-bar spans inside its own local
    /// "slot" Grid, or GradientTintVisual's overlay canvas inside its own
    /// host -- is a different grid entirely and lives outside this file, so
    /// it is out of scope by construction rather than by an exemption here.
    /// _demoOverlay is the one legitimate exception inside this file: 99 is
    /// a deliberate "cover any row/column count" value, not a stale "two
    /// rows" left behind by this change.
    /// </summary>
    [Fact]
    public void EveryCodeBehindRowSpanOnRootGrid_SpansAllThreeRows()
    {
        var exempt = new HashSet<string> { "_demoOverlay" };

        var src = ShellSource.Load("MainWindow.xaml.cs");
        var spans = src.Root.Calls("Grid.SetRowSpan")
            .Concat(src.Root.Calls("Microsoft.UI.Xaml.Controls.Grid.SetRowSpan"))
            .Select(call => (
                Target: call.ArgumentList.Arguments[0].ToString(),
                Span: call.ArgumentList.Arguments[1].ToString()))
            .Where(x => !exempt.Contains(x.Target))
            .ToList();

        Assert.NotEmpty(spans);
        Assert.All(spans, s => Assert.Equal("3", s.Span));
    }
}
