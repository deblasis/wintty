using System.Collections.Generic;

namespace Ghostty.Core.Settings;

/// <summary>
/// Hand-maintained registry of every config key the settings UI edits.
/// Grouped by Page and Section to match the sidebar + sub-section
/// layout. Tags drive the future search overlay: typing "color" in
/// search surfaces every entry whose Tags or Label contains "color".
///
/// When adding a new setting to the UI: add one entry here AND
/// (if applicable) to SettingsIndexTests.ExpectedKeys.
/// </summary>
public static class SettingsIndex
{
    public static readonly IReadOnlyList<SettingsEntry> All = new SettingsEntry[]
    {
        // ----- General -----
        new("auto-reload-config", "Auto-reload config",
            "Watch the config file for changes and reload automatically.",
            "General", "App Behavior",
            new[] { "reload", "watch", "config" },
            SettingType.Toggle),
        new("vertical-tabs", "Vertical tab bar",
            "Render tabs in a vertical sidebar instead of the default horizontal strip.",
            "General", "Tabs",
            new[] { "tabs", "vertical", "sidebar", "layout", "orientation" },
            SettingType.Toggle),
        new("command-palette-background", "Command palette backdrop",
            "Backdrop material for the command palette overlay (acrylic, mica, or opaque).",
            "General", "Command Palette",
            new[] { "palette", "acrylic", "mica", "backdrop", "opaque", "material" },
            SettingType.Combo),
        new("command-palette-group-commands", "Group palette commands by category",
            "Group entries in the command palette by category instead of listing them flat.",
            "General", "Command Palette",
            new[] { "palette", "group", "category", "organize" },
            SettingType.Toggle),

        // ----- Appearance / Window Mode -----
        new("window-theme", "Window mode",
            "Light, dark, or follow the system theme. Ghostty derives from terminal background.",
            "Appearance", "Window Mode",
            new[] { "theme", "dark", "light", "system", "chrome", "titlebar" },
            SettingType.Combo),

        // ----- Appearance / Font -----
        new("font-family", "Font family",
            "Monospace font used for terminal text.",
            "Appearance", "Font",
            new[] { "font", "text", "typography" },
            SettingType.Text),
        new("font-size", "Font size",
            "Point size of the terminal font.",
            "Appearance", "Font",
            new[] { "font", "size", "text", "zoom" },
            SettingType.Number),

        // ----- Appearance / Background Material -----
        new("background-opacity", "Background opacity",
            "Transparency of the terminal background (0 = fully transparent, 1 = opaque).",
            "Appearance", "Background Material",
            new[] { "opacity", "transparency", "alpha", "background" },
            SettingType.Slider),
        new("custom-shader", "Custom shader path",
            "Path to a GLSL post-process shader file.",
            "Appearance", "Background Material",
            new[] { "shader", "glsl", "effect", "post-process" },
            SettingType.Text),
        new("background-style", "Backdrop preset",
            "Solid (Mica), Frosted (Acrylic), or Crystal (zero blur).",
            "Appearance", "Background Material",
            new[] { "backdrop", "mica", "acrylic", "crystal", "material" },
            SettingType.Combo),
        new("background-blur-follows-opacity", "Blur follows opacity",
            "Automatically reduce blur as opacity increases.",
            "Appearance", "Background Material",
            new[] { "blur", "opacity", "acrylic" },
            SettingType.Toggle),
        new("background-tint-color", "Tint color",
            "Color overlaid on the acrylic backdrop.",
            "Appearance", "Background Material",
            new[] { "color", "tint", "acrylic", "background" },
            SettingType.Color),
        new("background-tint-opacity", "Tint opacity",
            "Strength of the acrylic tint color.",
            "Appearance", "Background Material",
            new[] { "tint", "opacity", "acrylic" },
            SettingType.Slider),
        new("background-luminosity-opacity", "Luminosity opacity",
            "Strength of the acrylic luminosity layer.",
            "Appearance", "Background Material",
            new[] { "luminosity", "opacity", "acrylic" },
            SettingType.Slider),

        // ----- Appearance / Gradient -----
        new("background-gradient-blend", "Gradient blend mode",
            "Whether the gradient renders over or under the terminal text.",
            "Appearance", "Gradient",
            new[] { "gradient", "blend", "overlay", "underlay" },
            SettingType.Combo),
        new("background-gradient-opacity", "Gradient opacity",
            "Strength of the gradient tint layer.",
            "Appearance", "Gradient",
            new[] { "gradient", "opacity" },
            SettingType.Slider),
        new("background-gradient-speed", "Gradient speed",
            "Animation speed multiplier for gradient motion effects.",
            "Appearance", "Gradient",
            new[] { "gradient", "speed", "animation" },
            SettingType.Slider),
        new("background-gradient-animation", "Gradient animation",
            "Motion effects applied to gradient points.",
            "Appearance", "Gradient",
            new[] { "gradient", "animation", "motion", "drift", "orbit" },
            SettingType.Custom),
        new("background-gradient-point", "Gradient points",
            "Position, color, and radius of each radial gradient source.",
            "Appearance", "Gradient",
            new[] { "gradient", "point", "color", "position" },
            SettingType.Custom),

        // ----- Colors / Theme -----
        new("theme", "Color theme",
            "Named theme file loaded from the themes directory. Supports light:X,dark:Y pairs.",
            "Colors", "Theme",
            new[] { "theme", "color", "palette", "scheme" },
            SettingType.Combo),

        // ----- Colors / Terminal Colors -----
        new("foreground", "Foreground",
            "Default text color.",
            "Colors", "Terminal Colors",
            new[] { "color", "text", "foreground" },
            SettingType.Color),
        new("background", "Background",
            "Default terminal background color.",
            "Colors", "Terminal Colors",
            new[] { "color", "background" },
            SettingType.Color),
        new("cursor-color", "Cursor color",
            "Color of the cursor glyph.",
            "Colors", "Terminal Colors",
            new[] { "color", "cursor" },
            SettingType.Color),
        new("selection-background", "Selection background",
            "Background color of selected text.",
            "Colors", "Terminal Colors",
            new[] { "color", "selection", "highlight" },
            SettingType.Color),

        // ----- Terminal -----
        new("scrollback-limit", "Scrollback limit",
            "Maximum bytes retained per terminal surface for scrollback (default 10 MB).",
            "Terminal", "Scrollback",
            new[] { "scrollback", "buffer", "history", "bytes", "memory" },
            SettingType.Number),
        new("cursor-style", "Cursor style",
            "Block, bar, or underline cursor shape.",
            "Terminal", "Cursor",
            new[] { "cursor", "shape", "style" },
            SettingType.Combo),
        new("cursor-style-blink", "Cursor blink",
            "Whether the cursor blinks.",
            "Terminal", "Cursor",
            new[] { "cursor", "blink", "animation" },
            SettingType.Toggle),
        new("mouse-hide-while-typing", "Hide mouse while typing",
            "Hide the mouse cursor during keyboard input.",
            "Terminal", "Mouse",
            new[] { "mouse", "cursor", "hide" },
            SettingType.Toggle),
    };
}
