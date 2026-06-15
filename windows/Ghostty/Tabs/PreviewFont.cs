using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Resolves the FontFamily used for tab previews: the user's configured terminal
/// font when set (so powerline / nerd glyphs render), else a monospace fallback.
/// WinUI silently falls back to the default monospace if the named family isn't
/// installed, so this never throws.
/// </summary>
internal static class PreviewFont
{
    private const string Fallback = "Consolas";

    public static FontFamily Resolve(string? configuredFamily)
        => new FontFamily(string.IsNullOrWhiteSpace(configuredFamily) ? Fallback : configuredFamily);
}
