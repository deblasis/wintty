using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// The launch a secondary process forwards to the primary when
/// single-instance mode is on: the working directory it was started in
/// and its full argv. Immutable; serialized to a wire format the primary
/// parses off the named pipe.
/// </summary>
public sealed record LaunchRequest(string WorkingDirectory, IReadOnlyList<string> Args)
{
    private const string Header = "V1";

    /// <summary>
    /// Length-prefixed UTF-8 encoding. Format:
    /// <c>V1\n</c> then each string as <c>&lt;utf8ByteCount&gt;:&lt;bytes&gt;</c>:
    /// working directory, then the arg count (as its own length-prefixed
    /// decimal string), then each arg. Length-prefixing means args may
    /// contain ANY bytes (newlines, colons, spaces, unicode) and still
    /// round-trip; there is no escaping and nothing to mis-split on. No
    /// JSON, so it is Native-AOT-safe with no reflection.
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        AppendField(sb, WorkingDirectory);
        AppendField(sb, Args.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var arg in Args)
            AppendField(sb, arg);
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        sb.Append(byteCount.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    /// <summary>
    /// Parse a serialized request. Returns false (never throws) on any
    /// malformed input so the server loop can drop a bad client without
    /// faulting.
    /// </summary>
    public static bool TryParse(string? s, out LaunchRequest? request)
    {
        request = null;
        if (s is null) return false;

        // Header line.
        var nl = s.IndexOf('\n');
        if (nl < 0 || s[..nl] != Header) return false;
        var rest = s[(nl + 1)..];
        var pos = 0;

        if (!TryReadField(rest, ref pos, out var cwd)) return false;
        if (!TryReadField(rest, ref pos, out var countStr)) return false;
        if (!int.TryParse(countStr, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count < 0)
            return false;

        var args = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadField(rest, ref pos, out var arg)) return false;
            args.Add(arg);
        }

        // Reject trailing garbage: everything must have been consumed.
        if (pos != rest.Length) return false;

        request = new LaunchRequest(cwd, args);
        return true;
    }

    private static bool TryReadField(string s, ref int pos, out string value)
    {
        value = string.Empty;
        var colon = s.IndexOf(':', pos);
        if (colon < 0) return false;

        var lenSpan = s.AsSpan(pos, colon - pos);
        if (!int.TryParse(lenSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var byteCount)
            || byteCount < 0)
            return false;

        var contentStart = colon + 1;
        // We measured the field in UTF-8 bytes but index a UTF-16 string;
        // walk forward counting bytes until we have consumed byteCount.
        var charCount = 0;
        var bytesSeen = 0;
        while (contentStart + charCount < s.Length && bytesSeen < byteCount)
        {
            bytesSeen += Utf8ByteLengthAt(s, contentStart + charCount, out var consumedChars);
            charCount += consumedChars;
        }
        if (bytesSeen != byteCount) return false;

        value = s.Substring(contentStart, charCount);
        pos = contentStart + charCount;
        return true;
    }

    // Returns the UTF-8 byte length of the (possibly surrogate-pair)
    // code unit(s) starting at index i, and how many UTF-16 chars it spans.
    private static int Utf8ByteLengthAt(string s, int i, out int consumedChars)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            consumedChars = 2;
            return 4; // astral plane code point
        }
        consumedChars = 1;
        if (c < 0x80) return 1;
        if (c < 0x800) return 2;
        return 3;
    }
}
