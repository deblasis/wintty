using System.Collections.Generic;
using System.Text;

namespace Ghostty.Core.Input;

/// <summary>
/// Renders a KeybindCatalog as a Markdown cheat sheet: a title, then a
/// "## Category" section per category with an Action/Shortcut table. Pure;
/// the WinUI cheat-sheet dialog calls this for copy-to-clipboard / save.
/// </summary>
public static class KeybindMarkdownExporter
{
    public const string Title = "# Keyboard Shortcuts";

    public static string Export(KeybindCatalog catalog) => Export(catalog.Categories);

    public static string Export(IReadOnlyList<KeybindCategory> categories)
    {
        var sb = new StringBuilder();
        sb.Append(Title).Append('\n');
        foreach (var category in categories)
        {
            sb.Append('\n').Append("## ").Append(category.Name).Append('\n').Append('\n');
            sb.Append("| Action | Shortcut |").Append('\n');
            sb.Append("| --- | --- |").Append('\n');
            foreach (var item in category.Items)
                sb.Append("| ").Append(Escape(item.Friendly))
                  .Append(" | ").Append(Escape(item.Label)).Append(" |").Append('\n');
        }

        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|");
}
