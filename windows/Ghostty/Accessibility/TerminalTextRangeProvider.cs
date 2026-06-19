using System;
using Ghostty.Core.Accessibility;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using CoreTextUnit = Ghostty.Core.Accessibility.TextUnit;
using WinTextUnit = Microsoft.UI.Xaml.Automation.Text.TextUnit;

namespace Ghostty.Accessibility;

/// <summary>
/// A UIA text range over the terminal document, backed by an immutable
/// <see cref="TextSpan"/>. All navigation math is delegated to the pure
/// <see cref="TextRangeNavigator"/>; this type only adapts WinUI projection
/// types. Read-only in this stage: selection and scroll mutators are no-ops.
/// </summary>
internal sealed partial class TerminalTextRangeProvider : ITextRangeProvider
{
    private readonly TerminalAutomationPeer _peer;
    private TextSpan _span;

    internal TerminalTextRangeProvider(TerminalAutomationPeer peer, TextSpan span)
    {
        _peer = peer;
        _span = span;
    }

    private TerminalDocument Doc => _peer.Document;

    private static CoreTextUnit MapUnit(WinTextUnit unit) => unit switch
    {
        WinTextUnit.Character => CoreTextUnit.Character,
        WinTextUnit.Format => CoreTextUnit.Character,
        WinTextUnit.Word => CoreTextUnit.Word,
        WinTextUnit.Line => CoreTextUnit.Line,
        WinTextUnit.Paragraph => CoreTextUnit.Line,
        _ => CoreTextUnit.Document,
    };

    public ITextRangeProvider Clone() => new TerminalTextRangeProvider(_peer, _span);

    public bool Compare(ITextRangeProvider other) =>
        other is TerminalTextRangeProvider o && o._span == _span;

    public int CompareEndpoints(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint)
    {
        var a = EndpointOf(_span, endpoint);
        var b = EndpointOf(((TerminalTextRangeProvider)targetRange)._span, targetEndpoint);
        return TextRangeNavigator.CompareEndpoints(a, b);
    }

    public void ExpandToEnclosingUnit(WinTextUnit unit) =>
        _span = TextRangeNavigator.ExpandToEnclosingUnit(Doc, _span, MapUnit(unit));

    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => null!;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        var match = Doc.Find(text, _span.Start, _span.End, backward, ignoreCase);
        return match is { } m ? new TerminalTextRangeProvider(_peer, m) : null!;
    }

    // Text attributes (foreground/background color, etc.) are added in a later
    // stage; null signals "no value" to clients until then.
    public object GetAttributeValue(int attributeId) => null!;

    public void GetBoundingRectangles(out double[] rectangles) => rectangles = Array.Empty<double>();

    public IRawElementProviderSimple GetEnclosingElement() => _peer.Provider;

    public string GetText(int maxLength)
    {
        var text = Doc.Slice(_span.Start, _span.End);
        return maxLength >= 0 && maxLength < text.Length ? text.Substring(0, maxLength) : text;
    }

    public int Move(WinTextUnit unit, int count)
    {
        // Collapse to the start, move that endpoint, then expand by one unit.
        var (offset, moved) = TextRangeNavigator.MoveEndpointByUnit(Doc, _span.Start, MapUnit(unit), count);
        _span = TextRangeNavigator.ExpandToEnclosingUnit(Doc, new TextSpan(offset, offset), MapUnit(unit));
        return moved;
    }

    public void MoveEndpointByRange(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint)
    {
        var value = EndpointOf(((TerminalTextRangeProvider)targetRange)._span, targetEndpoint);
        _span = SetEndpoint(_span, endpoint, value);
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, WinTextUnit unit, int count)
    {
        var from = EndpointOf(_span, endpoint);
        var (newOffset, moved) = TextRangeNavigator.MoveEndpointByUnit(Doc, from, MapUnit(unit), count);
        _span = SetEndpoint(_span, endpoint, newOffset);
        return moved;
    }

    public void Select() { /* read-only: selection is driven from the terminal, not UIA */ }

    public void AddToSelection() { }

    public void RemoveFromSelection() { }

    public void ScrollIntoView(bool alignToTop) { }

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();

    // ---- helpers ----------------------------------------------------------

    private static int EndpointOf(TextSpan span, TextPatternRangeEndpoint endpoint) =>
        endpoint == TextPatternRangeEndpoint.Start ? span.Start : span.End;

    private TextSpan SetEndpoint(TextSpan span, TextPatternRangeEndpoint endpoint, int value)
    {
        value = Doc.ClampOffset(value);
        if (endpoint == TextPatternRangeEndpoint.Start)
            return new TextSpan(value, Math.Max(value, span.End));
        return new TextSpan(Math.Min(span.Start, value), value);
    }
}
