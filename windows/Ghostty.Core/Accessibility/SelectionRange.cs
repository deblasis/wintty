namespace Ghostty.Core.Accessibility;

/// <summary>
/// Maps libghostty's selection offsets (<c>offset_start</c> / <c>offset_len</c>
/// from <c>ghostty_surface_read_selection</c>) to a document-bounded
/// <see cref="TextSpan"/>. Core documents these as linear flattened-viewport
/// offsets that can be imprecise for partially visible selections but always
/// stay within text bounds, so clamping is sufficient and safe.
/// </summary>
public static class SelectionRange
{
    public static TextSpan FromOffsets(uint offsetStart, uint offsetLen, int docLength)
    {
        var max = docLength < 0 ? 0 : docLength;
        var start = offsetStart > (uint)max ? max : (int)offsetStart;
        long endL = (long)offsetStart + offsetLen;
        var end = endL > max ? max : (int)endL;
        if (end < start) end = start;
        return new TextSpan(start, end);
    }
}
