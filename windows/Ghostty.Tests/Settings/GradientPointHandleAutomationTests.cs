using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The gradient drag handles were bare ContentControls: focusable by
/// keyboard, and absent from the UI Automation tree entirely, so nothing
/// reading the window could name them, read where they sat, or move them.
///
/// The vocabulary a client round-trips is pure and tested directly. The
/// wiring that gives the handles an identity lives in WinUI types this
/// project cannot reference, so it is scanned out of the shipped source -
/// the same fallback StripCloseAutomationTests uses, and for the same
/// reason: that file shipped once as bare title text, silently dropping
/// the close button, while its own assertions still passed.
/// </summary>
public class GradientPointHandleAutomationTests
{
    [Fact]
    public void Handle_IsNamedByItsOrdinal_OneBased()
    {
        Assert.Equal("Gradient point 1 of 4", GradientPointsLogic.DescribeHandle(0, 4));
        Assert.Equal("Gradient point 4 of 4", GradientPointsLogic.DescribeHandle(3, 4));
    }

    /// <summary>
    /// Spoken, not plotted: whole percents, and words a listener can place
    /// on a canvas without seeing it.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, "0% across, 0% down")]
    [InlineData(0.5f, 0.5f, "50% across, 50% down")]
    [InlineData(1f, 1f, "100% across, 100% down")]
    [InlineData(0.36f, 0.6f, "36% across, 60% down")]
    public void Position_ReadsAsWholePercents(float x, float y, string expected)
    {
        Assert.Equal(expected, GradientPointsLogic.DescribePosition(x, y));
    }

    /// <summary>
    /// Both halves of a percent round the same way. Banker's rounding sends
    /// 34.5 down and 35.5 up, which reads as a display bug from the outside.
    /// </summary>
    [Fact]
    public void Position_RoundsHalvesAwayFromZero()
    {
        Assert.Equal("35% across, 36% down", GradientPointsLogic.DescribePosition(0.345f, 0.355f));
    }

    [Fact]
    public void Position_ClampsBeforeDescribing()
    {
        Assert.Equal("0% across, 100% down", GradientPointsLogic.DescribePosition(-0.4f, 1.8f));
    }

    [Theory]
    [InlineData("80, 20", 0.8f, 0.2f)]
    [InlineData("80% across, 20% down", 0.8f, 0.2f)]
    [InlineData("80%, 20%", 0.8f, 0.2f)]
    [InlineData("  12.5 , 87.5  ", 0.125f, 0.875f)]
    public void BarePairsAndSpokenForm_BothParse(string text, float x, float y)
    {
        Assert.True(GradientPointsLogic.TryParsePosition(text, out var px, out var py));
        Assert.Equal(x, px, 3);
        Assert.Equal(y, py, 3);
    }

    /// <summary>
    /// A client asking for somewhere off the canvas wants the edge, not an
    /// exception: SetValue has no way to report a partial success.
    /// </summary>
    [Fact]
    public void OutOfRangeRequests_ClampToTheCanvas()
    {
        Assert.True(GradientPointsLogic.TryParsePosition("150, -20", out var x, out var y));
        Assert.Equal(1f, x);
        Assert.Equal(0f, y);
    }

    /// <summary>
    /// The parse is the only thing between a client and a config write, so
    /// it matches a whole position or nothing. Scanning for "the first two
    /// numbers anywhere" would make SetValue(element.Name) - the round-trip
    /// a client is most likely to try - read "Gradient point 1 of 4" as 1%
    /// and 4% and move the point. A decimal-comma locale's "12,5, 87,5" has
    /// to be refused for the same reason: parsing it as 12 and 5 is a wrong
    /// answer where a refusal is a safe one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("middle")]
    [InlineData("50")]
    [InlineData("Gradient point 1 of 4")]
    [InlineData("12,5, 87,5")]
    [InlineData("10, 20, 30")]
    [InlineData("1e2, 50")]
    public void AnythingThatIsNotAWholePosition_IsRefused(string? text)
    {
        Assert.False(GradientPointsLogic.TryParsePosition(text, out _, out _));
    }

    /// <summary>
    /// Reading a position and writing it straight back must not move the
    /// point. Asserted on the parsed coordinates, not on a re-description:
    /// re-describing an already-quantized value is green however coarse the
    /// quantization gets.
    /// </summary>
    [Fact]
    public void SpokenPosition_RoundTripsToTheSameCoordinates()
    {
        var spoken = GradientPointsLogic.DescribePosition(0.35f, 0.6f);
        Assert.True(GradientPointsLogic.TryParsePosition(spoken, out var x, out var y));
        Assert.Equal(0.35f, x, 4);
        Assert.Equal(0.6f, y, 4);
    }

    /// <summary>
    /// A bare ContentControl produces no automation element at all, so the
    /// handles were unreachable rather than merely unnamed. Reverting the
    /// subclass is the regression this exists to catch.
    /// </summary>
    [Fact]
    public void Canvas_BuildsHandlesThatHaveAPeer()
    {
        var editor = Source("GradientPointsEditor.xaml.cs");
        Assert.Contains("new GradientPointHandle", editor);
        Assert.DoesNotContain("new ContentControl", editor);

        var handle = Source("GradientPointHandle.cs");
        Assert.Contains("OnCreateAutomationPeer", handle);
        Assert.Contains("GradientPointHandleAutomationPeer", handle);
    }

    /// <summary>
    /// RenderCanvas clears every child, so an arrow key that rebuilds the
    /// canvas destroys the handle holding focus and the next key press goes
    /// nowhere. MovePoint is the in-place update drag already used.
    ///
    /// The whole handler is scanned bar the Delete case, whose rebuild is
    /// legitimate: a window that stopped at the switch would miss a
    /// RenderCanvas re-added next to MovePoint, which is where it used to be.
    /// </summary>
    [Fact]
    public void ArrowKeys_MoveInPlace_SoFocusSurvives()
    {
        var keyDown = Between(
            Source("GradientPointsEditor.xaml.cs"),
            "handle.KeyDown +=",
            "handle.PositionRequested =");

        Assert.Contains("MovePoint(capturedIndex)", keyDown);

        // Cut out the Delete arm, whose rebuild is legitimate, and require the
        // whole rest of the handler to be free of it. Windowing on landmarks
        // instead leaves gaps: a RenderCanvas re-added just above MovePoint
        // sat between two of them and went unnoticed.
        var deleteCase = Between(keyDown, "VirtualKey.Delete", "return;");
        Assert.DoesNotContain("RenderCanvas()", keyDown.Replace(deleteCase, string.Empty));
    }

    /// <summary>
    /// A client keeps its provider across a canvas rebuild. Trusting the
    /// index the handle was built with would move whichever point now sits
    /// at that index, so the resolved index has to be the one that is used.
    /// </summary>
    [Fact]
    public void ClientWrites_ResolveTheHandleByIdentity()
    {
        var requested = Between(
            Source("GradientPointsEditor.xaml.cs"),
            "handle.PositionRequested =",
            "handle.PointerPressed +=");

        Assert.Contains("_handles.IndexOf(h)", requested);
        Assert.Contains("_points[live] = point with", requested);
        // The snapshot index must not be what the write lands on.
        Assert.DoesNotContain("_points[h.Index]", requested);
        Assert.Contains("_dragIndex != -1", requested);
    }

    /// <summary>
    /// Both refusals return early, so without a signal a client reads back
    /// the old value believing its write landed.
    /// </summary>
    [Fact]
    public void RefusedWrites_ReachTheClient()
    {
        var requested = Between(
            Source("GradientPointsEditor.xaml.cs"),
            "handle.PositionRequested =",
            "handle.PointerPressed +=");
        Assert.Contains("return false", requested);

        var peer = Source("GradientPointHandleAutomationPeer.cs");
        Assert.Contains("throw new InvalidOperationException", peer);
        Assert.Contains("throw new ElementNotEnabledException", peer);
    }

    /// <summary>
    /// Removing a row destroys the button that was clicked, and this diff is
    /// what made that button a named, tabbable target in the first place.
    /// </summary>
    [Fact]
    public void RemovingARow_LandsFocusSomewhere()
    {
        var click = Between(
            Source("GradientPointsEditor.xaml.cs"),
            "remove.Click +=",
            "row.Children.Add(remove)");
        Assert.Contains("FocusHandle(", click);

        // The fallback is what keeps focus off the floor when the deleted
        // point had no neighbour.
        var focus = Between(
            Source("GradientPointsEditor.xaml.cs"),
            "private void FocusHandle(",
            "private void MovePoint(");
        Assert.Contains("AddPointButton.Focus", focus);
    }

    /// <summary>
    /// Every raise crosses the UIA boundary whether or not anyone advised,
    /// and a drag reaches it once per whole percent crossed.
    /// </summary>
    [Fact]
    public void ValueChanges_AreRaisedOnlyWhenSomeoneIsListening()
    {
        var peer = Source("GradientPointHandleAutomationPeer.cs");
        Assert.Contains("ListenerExists(AutomationEvents.PropertyChanged)", peer);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'");
        return source[from..to];
    }

    /// <summary>
    /// Read the shipped source with every comment removed, so no assertion
    /// here can be satisfied - or defeated - by prose naming the same thing.
    /// Block comments and trailing // count: a revert that leaves
    /// "// was: new GradientPointHandle" behind is the shape to see through.
    /// </summary>
    private static string Source(string suffix) => StripComments(ReadEmbedded(suffix));

    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var lines = withoutBlocks.Split('\n').Select(line =>
        {
            var slash = line.IndexOf("//", StringComparison.Ordinal);
            return slash >= 0 ? line[..slash] : line;
        });
        return string.Join('\n', lines);
    }

    /// <summary>
    /// These ride the Ghostty\**\*.cs glob that MarshalComplianceTests
    /// declares; they need no entry of their own, and adding one would be a
    /// duplicate-item error.
    /// </summary>
    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
