namespace Ghostty.Core.Version;

/// <summary>
/// Pure formatter for <see cref="Edition"/>. Public-repo path returns
/// "oss". Overlay extends the signature to take channel and adds the
/// channel-aware cases (Sponsor Stable, Pro Tip, etc.).
/// </summary>
public static class EditionLabel
{
    public static string Format(Edition edition) => edition switch
    {
        Edition.Oss => "oss",
        _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, null),
    };
}
