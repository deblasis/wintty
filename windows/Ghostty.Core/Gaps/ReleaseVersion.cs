using System.Text.RegularExpressions;

namespace Ghostty.Core.Gaps;

/// <summary>
/// A plain release version (<c>1.0.0</c>, <c>1.0.0-rc.12</c>) compared by SemVer
/// precedence with build metadata ignored.
/// </summary>
/// <remarks>
/// Deliberately not the cadence-stamped <c>--version</c> string
/// (<c>1.0.0-stable+&lt;sha&gt;</c>): SemVer orders that below <c>1.0.0</c>, which
/// would make every GA build look older than the release it is.
/// </remarks>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    private readonly string[] _pre;

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary><c>MAJOR.MINOR.PATCH</c> with pre-release and build metadata dropped.</summary>
    public string Core => $"{Major}.{Minor}.{Patch}";

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (text is null || text.Length > 64) return false;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].ValueSpan, out var major)
            || !int.TryParse(m.Groups[2].ValueSpan, out var minor)
            || !int.TryParse(m.Groups[3].ValueSpan, out var patch))
            return false;
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : [];
        version = new ReleaseVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // A release outranks any of its pre-releases.
        if (_pre.Length == 0) return other._pre.Length == 0 ? 0 : 1;
        if (other._pre.Length == 0) return -1;

        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i];
            var b = other._pre[i];
            var aNum = int.TryParse(a, out var an);
            var bNum = int.TryParse(b, out var bn);
            c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (c != 0) return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public override string ToString() => _pre.Length == 0 ? Core : Core + "-" + string.Join('.', _pre);

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
