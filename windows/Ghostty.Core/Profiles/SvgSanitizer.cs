using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Removes scriptable elements and attributes from user-supplied SVGs.
/// Bundled SVGs that ship in the build are trusted; this only runs on
/// the runtime <see cref="IconSpec.Path"/> case when the file ends in .svg.
/// </summary>
public static class SvgSanitizer
{
    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    private static readonly string[] BlockedElementLocalNames =
    [
        "script", "foreignObject", "iframe", "object", "embed", "a"
    ];

    public static string Sanitize(string svgText)
    {
        if (string.IsNullOrWhiteSpace(svgText)) return string.Empty;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(svgText, LoadOptions.None);
        }
        catch
        {
            return string.Empty;
        }

        if (doc.Root is null) return string.Empty;

        // Remove blocked elements anywhere in the tree.
        var blocked = doc.Root.DescendantsAndSelf()
            .Where(e => BlockedElementLocalNames.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var e in blocked) e.Remove();

        // Remove event handler attributes (on*) and external hrefs.
        foreach (var e in doc.Root.DescendantsAndSelf())
        {
            var attrs = e.Attributes().ToList();
            foreach (var a in attrs)
            {
                if (a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    a.Remove();
                    continue;
                }
                if (a.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase)
                    && IsExternalHref(a.Value))
                {
                    a.Remove();
                }
            }
        }

        using var writer = new StringWriter();
        doc.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    private static bool IsExternalHref(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.StartsWith("#")) return false;  // intra-doc fragment
        return value.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase);
    }
}
