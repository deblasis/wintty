using System;
using Ghostty.Core.Config;
using Ghostty.Core.Themes;

namespace Ghostty.Services;

/// <summary>
/// The command palette's theme browse, pointed at the real app: previews go
/// onto every live terminal and the chrome through <see cref="ConfigService"/>,
/// and a confirm is one <c>theme</c> write through the same editor and writer
/// the Settings Colors page uses, so it passes the same config write guard.
/// </summary>
internal sealed class PaletteThemeTarget : IThemePreviewTarget
{
    /// <summary>The config key a confirmed palette theme is written to.</summary>
    internal const string ThemeKey = "theme";

    private readonly ConfigService _config;
    private readonly SettingsConfigWriter _writer;
    private readonly IConfigFileEditor _editor;

    public PaletteThemeTarget(ConfigService config, SettingsConfigWriter writer, IConfigFileEditor editor)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    public ThemePreviewColors CaptureColors() => new(
        _config.ForegroundColor,
        _config.BackgroundColor,
        _config.CursorColor,
        _config.CursorTextColor,
        _config.AnsiPalette);

    public bool ApplyPreview(string themeName) => _config.PreviewTheme(themeName);

    public void Revert(ThemePreviewColors? colors) => _config.RevertThemePreview(colors);

    public void Commit(string themeName)
    {
        try
        {
            // One key, one write, then the reload that puts the committed
            // config (now naming this theme) on every view. A single theme
            // replaces a light/dark pair, as the Colors page does when a pair
            // collapses to one; explicit colour keys in the file still win
            // over the theme, exactly as they did during the preview.
            _writer.Write(() => _editor.SetValue(ThemeKey, themeName), ThemeKey);
        }
        catch (InvalidOperationException)
        {
            // --no-config refuses every write. Nothing was persisted, so the
            // committed config is still the truth: reload from it rather than
            // leave the views on a preview nobody saved.
            _config.Reload();
        }

        // A reload that did not run (teardown, or a failed build of the new
        // config) leaves the app on the preview. Never leave it there.
        if (_config.IsPreviewingTheme) _config.RevertThemePreview(null);
    }
}
