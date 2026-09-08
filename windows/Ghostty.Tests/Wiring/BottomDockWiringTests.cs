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
    public void DockSizeChange_NotesALayoutSwitch_OnVisibleTerminals()
    {
        var src = ShellSource.Load("MainWindow.xaml.cs");
        var handler = src.Method("OnNotificationDockSizeChanged");
        var calls = handler.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString()).ToList();
        Assert.Contains(calls, c => c.EndsWith("NoteLayoutSwitch"));
    }
}
