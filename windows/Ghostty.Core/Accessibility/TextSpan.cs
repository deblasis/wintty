namespace Ghostty.Core.Accessibility;

/// <summary>A half-open text range <c>[Start, End)</c> in UTF-16 offsets.</summary>
public readonly record struct TextSpan(int Start, int End)
{
    public int Length => End - Start;
    public bool IsDegenerate => Start == End;
}

/// <summary>
/// Text units the range provider supports in this stage. Word and Page are
/// added in a later stage; the WinUI adapter maps the projection's TextUnit
/// onto this enum and treats unsupported units as the nearest supported one.
/// </summary>
public enum TextUnit
{
    Character,
    Word,
    Line,
    Document,
}
