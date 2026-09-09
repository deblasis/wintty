using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Where the sponsor update pill is allowed to live.
///
/// A downstream tier renders the pill into a presenter this shell owns. For
/// as long as there was one presenter it sat inside <c>VerticalTitleBar</c>,
/// which is Collapsed unless <c>vertical-tabs</c> is on, so on the default
/// layout the pill was never rendered at all: not Checking, not Update
/// Available, not Restart to Complete Update
/// (deblasis/wintty-release#655).
///
/// So there are two presenters, one per title row, and the shell moves the
/// content to whichever row is on screen. These guards hold that shape: that
/// the horizontal half exists at all, that the two are separately named, that
/// the switch re-homes the content, and that the horizontal one is kept clear
/// of the caption buttons. The pill's own behaviour lives in the tier that
/// owns it and is not visible from here.
/// </summary>
public class SponsorOverlayHostWiringTests
{
    private const string MainWindowXaml = "Ghostty.Tests.MainWindow.xaml";
    private const string TabHostXaml = "Ghostty.Tests.Tabs.TabHost.xaml";
    private const string MainWindowSource = "MainWindow.xaml.cs";
    private const string TitleBarSource = "Shell.TitleBarCoordinator.cs";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The half that did not exist. Without it the pill has nowhere to render
    /// in the layout almost every user is on.
    /// </summary>
    [Fact]
    public void The_horizontal_strip_carries_an_overlay_host()
    {
        var host = Named(Parse(TabHostXaml), "SponsorOverlayHostHorizontal");

        Assert.True(
            host is not null,
            "TabHost.xaml has no SponsorOverlayHostHorizontal. The horizontal "
            + "strip is the default layout, and with no presenter there the "
            + "update pill renders in no layout but vertical-tabs.");
        Assert.Equal("ContentPresenter", host!.Name.LocalName);

        // Beside the drag region, never inside it. MainWindow hands
        // CustomDragRegion to SetTitleBar, and the SetTitleBar element's
        // subtree is caption input to the OS: a pill inside it rendered but
        // could not take a click, and the pill IS a click target. The
        // new-tab control beside it in the same footer is the precedent.
        Assert.True(
            !host.Ancestors().Any(a => NameOf(a) == "CustomDragRegion"),
            "The horizontal host must sit OUTSIDE CustomDragRegion, which is "
            + "what MainWindow hands to SetTitleBar: inside it the pill is "
            + "caption input and cannot take a click.");
        Assert.True(
            host.Ancestors().Any(a => NameOf(a) == "FooterRoot"),
            "The horizontal host belongs in the strip footer (FooterRoot), "
            + "inside the title row but beside its drag region.");

        // Nothing hides it on its own account: the layout hides the whole
        // host, which is the one visibility decision there should be.
        Assert.Null(host.Attribute("Visibility"));
        foreach (var ancestor in host.Ancestors())
        {
            Assert.True(
                (string?)ancestor.Attribute("Visibility") != "Collapsed",
                $"<{ancestor.Name.LocalName} x:Name=\"{NameOf(ancestor)}\"> collapses the "
                + "horizontal overlay host, which is the whole defect this pair exists to fix.");
        }
    }

    /// <summary>
    /// The vertical half, and the reason the names differ. Two presenters that
    /// shared a name would be a duplicate x:Name the moment they were in one
    /// namescope, and a single name is what let a lookup believe it had found
    /// the host when it had found the hidden one.
    /// </summary>
    [Fact]
    public void The_vertical_host_is_separately_named_and_lives_in_the_title_row()
    {
        var window = Parse(MainWindowXaml);
        var host = Named(window, "SponsorOverlayHost");

        Assert.True(host is not null, "MainWindow.xaml has no SponsorOverlayHost.");
        Assert.Equal("ContentPresenter", host!.Name.LocalName);

        // Beside the drag region, never inside it, for the same reason the
        // horizontal one is: VerticalTitleDragRegion is what MainWindow
        // hands to SetTitleBar, and its subtree is caption input.
        Assert.True(
            !host.Ancestors().Any(a => NameOf(a) == "VerticalTitleDragRegion"),
            "The vertical host must sit OUTSIDE VerticalTitleDragRegion, "
            + "which is what MainWindow hands to SetTitleBar: inside it the "
            + "pill is caption input and cannot take a click.");
        Assert.True(
            host.Ancestors().Any(a => NameOf(a) == "VerticalTitleBar"),
            "The vertical host belongs in the vertical title row, beside "
            + "the drag region and ahead of the caption column.");

        // The row it is in is Collapsed by default, which is the fact that
        // makes a second presenter necessary rather than optional.
        var row = window.Descendants().Single(e => NameOf(e) == "VerticalTitleBar");
        Assert.Equal("Collapsed", (string?)row.Attribute("Visibility"));

        // And the horizontal one is not also in this file under this name.
        Assert.Null(Named(window, "SponsorOverlayHostHorizontal"));
    }

    /// <summary>
    /// A switch has to move the content, or the pill renders into the row that
    /// just left and disappears with it.
    /// </summary>
    [Fact]
    public void The_layout_switch_re_homes_the_overlay_content()
    {
        var animate = ShellSource.Load(MainWindowSource).Method("AnimateTabLayoutTo");

        var rehome = animate.Calls("ReHomeSponsorOverlay");
        Assert.True(
            rehome.Count == 1,
            $"AnimateTabLayoutTo must re-home the overlay content exactly once; found {rehome.Count}.");

        // After the flag, not before: the re-home reads _verticalTabsVisible
        // to decide which presenter is the arriving one, so running it first
        // parks the pill back in the row that is leaving.
        var flag = animate.AssignsTo("_verticalTabsVisible").ToList();
        Assert.True(flag.Count == 1, $"expected one _verticalTabsVisible assignment, found {flag.Count}");
        Assert.True(
            flag[0].SpanStart < rehome[0].SpanStart,
            "ReHomeSponsorOverlay reads _verticalTabsVisible; it must run after the flag is set.");
    }

    /// <summary>
    /// The order inside the move. A UIElement has one parent, so handing it to
    /// the arriving presenter while the leaving one still holds it throws
    /// instead of moving it, and the pill is lost for the rest of the session.
    /// </summary>
    [Fact]
    public void The_idle_presenter_is_cleared_before_the_active_one_is_filled()
    {
        var rehome = ShellSource.Load(MainWindowSource).Method("ReHomeSponsorOverlay");

        var clear = rehome.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "idle.Content"
                        && a.Right.ToString() == "null")
            .ToList();
        var fill = rehome.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "active.Content")
            .ToList();

        Assert.True(clear.Count == 1, $"expected one 'idle.Content = null', found {clear.Count}");
        Assert.True(fill.Count == 1, $"expected one write to active.Content, found {fill.Count}");
        Assert.True(
            clear[0].SpanStart < fill[0].SpanStart,
            "Clear the leaving presenter first: a UIElement cannot be in two of them at once.");
    }

    /// <summary>
    /// The horizontal presenter's cell runs to the footer's right edge, which
    /// is the window's, so without the inset it draws under the OS caption
    /// buttons. The vertical row's presenter is cleared by the caption column
    /// it sits ahead of and needs no equivalent.
    /// </summary>
    [Fact]
    public void The_caption_inset_sync_moves_the_horizontal_host()
    {
        var sync = ShellSource.Load(TitleBarSource).Method("SyncCaptionInset");
        var call = sync.Call("_horizontalTabHost.SyncSponsorOverlayInset");

        // The same number the drag region's MinWidth is set from. A second
        // source for it is a second thing that can disagree with the caption
        // lane, which is the defect the inset column was consolidated to fix.
        Assert.Equal("dip", call.Arg(0));

        // And the captionless (quake) window zeroes it: there are no buttons
        // there, so the XAML default would hold an empty lane open.
        var captionless = ShellSource.Load(TitleBarSource).Method("SetCaptionless");
        Assert.Equal("0", captionless.Call("_horizontalTabHost.SyncSponsorOverlayInset").Arg(0));
    }

    private static XElement Parse(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(logicalName);
        Assert.True(stream is not null, $"embedded resource '{logicalName}' is missing");
        using var reader = new StreamReader(stream!);
        return XDocument.Parse(reader.ReadToEnd()).Root!;
    }

    private static XElement? Named(XElement root, string name) =>
        root.DescendantsAndSelf().SingleOrDefault(e => NameOf(e) == name);

    private static string? NameOf(XElement element) => (string?)element.Attribute(Xaml + "Name");
}
