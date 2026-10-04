using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Controls.Settings;
using Ghostty.Core.Settings;
using Ghostty.Core.Config;
using Ghostty.Core.DirectWrite;
using Ghostty.Core.Shell;
using Ghostty.Core.Settings;
using Ghostty.Logging;
using Ghostty.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Ghostty.Settings.Pages;

internal sealed partial class AppearancePage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly SettingsConfigWriter _writer;
    private readonly SearchableList _fontList;
    private bool _loading = true;
    // Counts Reload() invocations we initiated ourselves. Each one will
    // eventually re-enter OnConfigChanged via the dispatcher queue; we
    // decrement to skip that re-seed (the editor already has the values
    // we just wrote). External config file edits never touch this so they
    // still re-seed normally.
    private int _expectingOwnReloads;

    // The frame-style combo's first entry stands for the key being absent,
    // which is what "match the backdrop" means. There is no value that says
    // unset, so the entry carries no tag and choosing it removes the line.
    private const string MatchBackdropTag = "";

    public AppearancePage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        _writer = new SettingsConfigWriter(configService, StaticLoggers.SettingsConfigWriter);
        InitializeComponent();

        // Set here rather than in XAML because AppIdentity is internal,
        // and x:Bind's AOT-generated code would require the type to be
        // public. Same constraint CommandPaletteControl documents.
        WindowThemeProductLabel.Text = Ghostty.Core.AppIdentity.ProductName;

        PopulateShaderGallery();

        _fontList = new SearchableList(FontFamilySearch, chosen => OnValueChanged("font-family", chosen));

        // Every control this page can write the config from is seeded by one
        // method, called from here with _loading still true and again from
        // OnConfigChanged under the same guard. One seeding path is the whole
        // point: a control seeded in only one of the two is a control that
        // describes the config as of whenever the page happened to be built.
        SeedFromConfig();

        GradientEditor.PointsChanged += (_, _) => WriteAllPoints();

        _loading = false;

        // Subscribe in Loaded rather than the ctor: SettingsWindow caches and
        // reuses page instances, so the ctor runs once while Loaded/Unloaded
        // fire on every navigation. A ctor-time subscription paired with an
        // Unloaded unsubscribe would be dropped the first time the user
        // navigates away and never restored on return.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        LoadFontsAsync();
    }

    /// <summary>
    /// Put every control on this page back in step with the config, and
    /// write nothing.
    ///
    /// Callers own the <c>_loading</c> guard. That guard is load-bearing
    /// here and not merely tidy: seeding a control fires that control's own
    /// handler, and every one of those handlers writes the config key the
    /// control is showing -- which, mid-re-seed, is the value the page is
    /// halfway through adopting. Without the guard a reload writes the file
    /// back what it just read, and does it in whatever order the seeds run.
    ///
    /// The Windows-only properties live on the concrete ConfigService, so the
    /// fakes a test hands in fall back to the same defaults the constructor
    /// has always used and pick up nothing else.
    /// </summary>
    private void SeedFromConfig()
    {
        OpacitySlider.Value = _configService.BackgroundOpacity;
        SelectWindowTheme(_configService.WindowTheme);
        SeedShaderPath();
        SeedFontFamily();

        if (_configService is not ConfigService cs)
        {
            SelectComboByTag(BackgroundStyleCombo, BackdropStyles.Default);
            SelectComboByTag(FrameStyleCombo, MatchBackdropTag);
            return;
        }

        SelectComboByTag(BackgroundStyleCombo, cs.BackgroundStyle, BackdropStyles.Default);

        // FrameStyle answers the resolved value, so it cannot tell an
        // unset key from one set to what the backdrop already says. The
        // file can, and the two show as different entries here.
        SelectComboByTag(
            FrameStyleCombo,
            cs.IsConfiguredInFile("frame-style") ? cs.FrameStyle : MatchBackdropTag);

        // Seed power saver mode from config, defaulting to "auto".
        var powerMode = cs.GetRawFileValue("power-saver-mode");
        if (string.IsNullOrWhiteSpace(powerMode)) powerMode = "auto";
        SelectComboByTag(PowerSaverModeCombo, powerMode.Trim().ToLowerInvariant());

        // Seed the Animations lever from config, defaulting to "system".
        SeedAnimationsCombo(cs);

        // NoColorOverride is already normalized to one of notify/strip/keep.
        SelectComboByTag(NoColorOverrideCombo, cs.NoColorOverride);

        BlurFollowsOpacityToggle.IsOn = cs.BackgroundBlurFollowsOpacity;
        SeedTintColor(cs);
        TintOpacitySlider.Value = cs.BackgroundTintOpacity ?? 0.3;
        LuminosityOpacitySlider.Value = cs.BackgroundLuminosityOpacity ?? 0.3;

        // Seed font size before the guard can matter, or the ValueChanged
        // handler would write back the value this method just read.
        FontSizeBox.Value = cs.FontSize;

        SeedGradient(cs);
    }

    // The tint row reads as "unset" unless the file carries the key, the
    // same rule the Colors rows follow: an inherited default is not something
    // the user set, and writing it back is how a default becomes an override.
    private void SeedTintColor(ConfigService cs)
    {
        if (!cs.IsConfiguredInFile("background-tint-color"))
        {
            TintColorPicker.Color = "";
            TintColorResetButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (cs.BackgroundTintColor.HasValue)
        {
            var c = cs.BackgroundTintColor.Value;
            TintColorPicker.Color = new Rgb(c.R, c.G, c.B).ToHex();
        }
        TintColorResetButton.Visibility = Visibility.Visible;
    }

    // The gradient card and everything under it, from the same resolved
    // values the constructor has always read.
    private void SeedGradient(ConfigService cs)
    {
        var points = cs.GradientPoints;
        GradientEnabledToggle.IsOn = points.Count > 0;
        GradientSettingsPanel.Visibility = points.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Load existing points into editor.
        GradientEditor.SetPoints(points
            .Select(p => new GradientPointModel(p.X, p.Y, p.Color, p.Radius))
            .ToList());

        // Parse animation mode into radio + checkboxes.
        var anim = cs.GradientAnimation;
        var effects = anim.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Select position radio.
        string[] positionModes = ["", "drift", "orbit", "wander", "bounce"];
        for (int i = 0; i < positionModes.Length; i++)
        {
            if (effects.Contains(positionModes[i]) || (i == 0 && !effects.Any(e => positionModes.Contains(e))))
            {
                PositionAnimRadio.SelectedIndex = i;
                break;
            }
        }

        BreatheCheck.IsChecked = effects.Contains("breathe");
        ColorCycleCheck.IsChecked = effects.Contains("color-cycle");

        GradientSpeedSlider.Value = cs.GradientSpeed;
        GradientOpacitySlider.Value = cs.GradientOpacity;

        SelectComboByTag(GradientBlendCombo, cs.GradientBlend);
    }

    // font-family has no typed accessor on IConfigService, and the box has to
    // show what is in use rather than sit empty. LoadFontsAsync sets it again
    // once the enumeration lands; both read the same value, so the later one
    // is a no-op unless the config moved in between.
    private void SeedFontFamily()
    {
        if (_configService is ConfigService cs && !string.IsNullOrEmpty(cs.FontFamily))
        {
            FontFamilySearch.Text = cs.FontFamily;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged += OnConfigChanged;
        // Page instances are cached, so returning to this page fires Loaded
        // without the ctor (or any seeding) running again. ConfigChanged is
        // unsubscribed in OnUnloaded, so edits made while the page was away
        // were missed: re-read the file instead of trusting the last value
        // this page wrote.
        SeedShaderPath();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged -= OnConfigChanged;
    }

    private void SelectWindowTheme(string theme)
    {
        // The combo only carries the preferred spelling, so fold the alias
        // before matching or an unmigrated config falls through to "auto".
        var wanted = WindowThemeAlias.Canonicalize(theme);

        foreach (ComboBoxItem item in WindowThemeCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                WindowThemeCombo.SelectedItem = item;
                return;
            }
        }
        // Default to "auto" if the value is unrecognized.
        WindowThemeCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Select the item carrying <paramref name="tag"/>, falling back to
    /// <paramref name="fallbackTag"/> and only then to the first item.
    ///
    /// The first item is not a neutral landing place. On the backdrop
    /// combo it is solid, so a value this page could not match -- a
    /// misspelling, or a key the config knows and the combo does not --
    /// showed the user a material the window was not drawing.
    /// </summary>
    private static void SelectComboByTag(ComboBox combo, string tag, string? fallbackTag = null)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (fallbackTag is not null
            && !string.Equals(fallbackTag, tag, StringComparison.OrdinalIgnoreCase))
        {
            SelectComboByTag(combo, fallbackTag);
            return;
        }

        combo.SelectedIndex = 0;
    }

    private void LoadFontsAsync()
    {
        FontFamilySearch.PlaceholderText = "Loading fonts...";
        var dispatcher = DispatcherQueue;
        Task.Run(() =>
        {
            var fonts = EnumerateSystemFonts();
            dispatcher.TryEnqueue(() =>
            {
                _fontList.SetItems(fonts);
                FontFamilySearch.PlaceholderText = $"Search {fonts.Count} fonts...";

                // Display the currently-configured font so the user sees
                // what's in use, not an empty placeholder. Reading from
                // the concrete ConfigService since font-family isn't on
                // IConfigService.
                if (_configService is ConfigService cs && !string.IsNullOrEmpty(cs.FontFamily))
                {
                    FontFamilySearch.Text = cs.FontFamily;
                }
            });
        });
    }

    // Thin adapter delegating to the shared Ghostty.Core helper.
    // Keeps JetBrains Mono injection at this layer because the
    // embedded font list is a Ghostty UI decision, not a DWrite
    // enumeration detail. The DWrite vtable dispatch lives in
    // Ghostty.Core.DirectWrite.DWriteFontEnumerator and is covered
    // by DWriteFontFamilyEquivalenceTest.
    private static List<string> EnumerateSystemFonts()
    {
        var families = DWriteFontEnumerator.EnumerateMigrated();

        // Ghostty embeds JetBrains Mono in the binary so it's always
        // available even if not installed on the system.
        if (!families.Contains("JetBrains Mono", StringComparer.OrdinalIgnoreCase))
        {
            families.Add("JetBrains Mono");
            families.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return families;
    }

    private void OnValueChanged(string key, string value)
    {
        if (_loading) return;
        _writer.Write(() => _editor.SetValue(key, value), key);
    }

    private void FontSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        OnValueChanged("font-size", sender.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Opacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-opacity", e.NewValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void WindowTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("window-theme", item.Tag?.ToString() ?? "auto");
    }

    // Read from the file rather than from the merged config: this box edits
    // what is written down, and a default the user never set would be written
    // back the moment the box loses focus.
    //
    // custom-shader is repeatable and this is one box, so a config with several
    // entries can only show one of them. It shows the FIRST, and writing sets
    // the whole list to just that value, so what the box displays is always
    // what the setting is. Reading the first and writing the last -- which is
    // where SetValue lands -- would leave the entry the user was looking at
    // untouched and destroy one they never saw.
    private void SeedShaderPath()
    {
        var values = _editor.GetRepeatableValues("custom-shader");
        ShaderPathBox.Text = values.Length > 0 ? values[0] : string.Empty;
        _shaderPathWritten = ShaderPathBox.Text;
        _shaderPathExtraEntries = values.Length > 1;
        SyncShaderUiForPath(ShaderPathBox.Text);
    }

    // ── Shader gallery ─────────────────────────────────────────────────────

    // Gallery entries keyed by the absolute installed path of their shader
    // file, so a configured path can be mapped back to its combo item.
    // Case-insensitive: the picker preselects and commits paths with
    // OrdinalIgnoreCase semantics, and a casing mismatch would otherwise
    // classify a gallery pick as "From file".
    private readonly Dictionary<string, ShaderGalleryEntry> _shaderGalleryByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private void PopulateShaderGallery()
    {
        // NativeAOT-safe manifest binding (see ShaderGalleryJson). Idempotent;
        // first consumer to run wires it.
        Ghostty.Core.Settings.ShaderGallery.ManifestParser ??= ShaderGalleryJson.Parse;

        if (ShaderGallery.Entries.Count == 0)
        {
            StaticLoggers.SettingsConfigWriter.LogInformation(
                "shader gallery empty: {Detail} (base: {Base})",
                ShaderGallery.LoadDetail, AppContext.BaseDirectory);
        }
        // The picker window renders the entries; this page only needs the
        // path -> entry map to classify a configured path as gallery vs file.
        foreach (var entry in ShaderGallery.Entries)
        {
            _shaderGalleryByPath[ShaderGallery.AbsolutePathFor(entry)] = entry;
        }
    }

    /// <summary>
    /// Mirrors the configured shader path into the three-state selector:
    /// empty = None, a gallery path = From gallery (named), anything else =
    /// From file (path shown in the box). No writes; callers own committing.
    /// </summary>
    private void SyncShaderUiForPath(string path)
    {
        // Radio selection fires ShaderMode_SelectionChanged, which writes
        // for "None"; a pure UI mirror must not re-commit what it just read.
        var loading = _loading;
        _loading = true;
        try
        {

        if (string.IsNullOrWhiteSpace(path))
        {
            ShaderNoneRadio.IsChecked = true;
            GalleryPickRow.Visibility = Visibility.Collapsed;
            FilePickRow.Visibility = Visibility.Collapsed;
            GalleryPickLabel.Text = "No shader selected";
        }
        else if (_shaderGalleryByPath.TryGetValue(path, out var entry))
        {
            ShaderGalleryRadio.IsChecked = true;
            GalleryPickRow.Visibility = Visibility.Visible;
            FilePickRow.Visibility = Visibility.Collapsed;
            GalleryPickLabel.Text = $"{entry.Name} — {entry.Description}";
            ShaderPathBox.Text = path;
        }
        else
        {
            ShaderFileRadio.IsChecked = true;
            GalleryPickRow.Visibility = Visibility.Collapsed;
            FilePickRow.Visibility = Visibility.Visible;
            ShaderPathBox.Text = path;
        }
        }
        finally
        {
            _loading = loading;
        }
    }

    private void ShaderMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButtons buttons) return;
        if (buttons.SelectedItem is not RadioButton radio) return;

        switch (radio.Name)
        {
            case nameof(ShaderNoneRadio):
                GalleryPickRow.Visibility = Visibility.Collapsed;
                FilePickRow.Visibility = Visibility.Collapsed;
                ShaderPathBox.Text = string.Empty;
                WriteShaderPathValue(string.Empty);
                break;

            case nameof(ShaderGalleryRadio):
                GalleryPickRow.Visibility = Visibility.Visible;
                FilePickRow.Visibility = Visibility.Collapsed;
                // Selecting the mode opens the picker: that is where a
                // gallery shader gets chosen and previewed. Cancelling
                // leaves the mode checked and the config untouched.
                OpenShaderPicker();
                break;

            case nameof(ShaderFileRadio):
                GalleryPickRow.Visibility = Visibility.Collapsed;
                FilePickRow.Visibility = Visibility.Visible;
                // No write of its own: the path box is the source of truth
                // for a custom file, exactly as before.
                break;
        }
    }

    private void ShaderGalleryChoose_Click(object sender, RoutedEventArgs e) => OpenShaderPicker();

    // The one picker instance while it is open. Both entry points
    // (selecting the gallery radio and Choose...) funnel through
    // OpenShaderPicker, so a second ask activates the live window instead
    // of stacking another one on top. Cleared by Closed, not Unloaded:
    // the page is cached across navigations while the picker is modeless
    // and outlives them.
    private ShaderPickerWindow? _shaderPicker;

    private void OpenShaderPicker()
    {
        if (_shaderPicker is { } open)
        {
            // Activate alone does not reliably restore a minimized window;
            // Show brings it back first.
            open.AppWindow?.Show();
            open.Activate();
            return;
        }

        var picker = new ShaderPickerWindow
        {
            CurrentPath = _shaderGalleryByPath.ContainsKey(_shaderPathWritten)
                ? _shaderPathWritten
                : null,
        };
        _shaderPicker = picker;

        // The picker is a top-level window the OS would keep alive after
        // the app tears down; parent its lifetime to the settings window
        // it was opened from (same pattern as the about window).
        var owner = (Application.Current as App)?.SettingsWindow;
        void OnOwnerClosed(object? s, WindowEventArgs e) => picker.Close();
        if (owner is not null) owner.Closed += OnOwnerClosed;

        picker.Closed += (sender, args) =>
        {
            _shaderPicker = null;
            if (owner is not null) owner.Closed -= OnOwnerClosed;
            if (picker.PickedPath is { } path)
            {
                WriteShaderPathValue(path);
            }
            // Mirror unconditionally, not only on commit: cancelling must
            // not leave the radios claiming a pick the config never got.
            // _shaderPathWritten is the post-write truth either way.
            SyncShaderUiForPath(_shaderPathWritten);
        };
        picker.Activate();
    }

    private async void ShaderBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            // Window.Current is null in WinUI 3 desktop apps; map the page's
            // window to an HWND for the picker's COM initializer (same recipe
            // as IconPickerDialog).
            var windowId = XamlRoot.ContentIslandEnvironment.AppWindowId;
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(windowId);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".glsl");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            ShaderPathBox.Text = file.Path;
            WriteShaderPathValue(file.Path);
            SyncShaderUiForPath(file.Path);
        }
        catch (Exception ex)
        {
            // async void: swallow and log instead of tearing down the process.
            StaticLoggers.SettingsConfigWriter.LogInformation(
                "shader browse failed: {Message}", ex.Message);
        }
    }

    // Writes the custom-shader key with the same semantics as the path box's
    // LostFocus (collapse warning, success-checked guards), shared by the box,
    // the browse button, and the gallery combo.
    private void WriteShaderPathValue(string value)
    {
        if (value == _shaderPathWritten) return;

        if (_shaderPathExtraEntries)
        {
            StaticLoggers.SettingsConfigWriter.LogInformation(
                "custom-shader had more entries than the Appearance box can show; " +
                "editing it collapses them to the one shown");
        }

        var values = value.Length > 0 ? new[] { value } : System.Array.Empty<string>();
        var result = _writer.Write(
            () => _editor.SetRepeatableValues("custom-shader", values),
            "custom-shader");

        if (!result.WriteSucceeded) return;

        _shaderPathWritten = value;
        _shaderPathExtraEntries = false;
        if (result.Reloaded) _expectingOwnReloads++;
    }

    // ── Shader preview ─────────────────────────────────────────────────────
    // The live preview moved into ShaderPickerWindow (gallery mode). This
    // page no longer hosts a preview surface, so there is nothing to create
    // or dispose here; the picker window owns its surface's lifetime.

    // The last value this page put in the file, or seeded from it. Blur fires
    // on every pass through the page, including tab-through and the window
    // closing, so an unconditional write here rewrote custom-shader every time
    // - and while the box was never seeded, it rewrote it to empty, silently
    // dropping a configured shader.
    private string _shaderPathWritten = string.Empty;

    // Whether the file had more custom-shader lines than this box can show. Only
    // used to log that writing collapses them, since the box cannot represent
    // them and silently dropping them would be worse unexplained.
    private bool _shaderPathExtraEntries;

    private void ShaderPath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not TextBox tb) return;

        var value = tb.Text ?? string.Empty;
        // Compare before writing: blur fires on tab-through and window close,
        // so an unconditional write rewrites the key on every pass. The
        // shared writer re-checks for its other callers (gallery, browse).
        if (value == _shaderPathWritten) return;

        // Shared with the gallery combo and browse button: collapse warning,
        // write, and the success-checked guards (a failed write must not
        // advance the guard, or retries from this page read as "unchanged").
        WriteShaderPathValue(value);
        SyncShaderUiForPath(value);
    }

    private void BackgroundStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("background-style", item.Tag?.ToString() ?? BackdropStyles.Default);
    }

    private void FrameStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item }) return;

        // OnValueChanged owns the seeding guard for the write; the removal
        // below bypasses it, and seeding the combo during construction would
        // otherwise comment the user's frame-style out of their config.
        if (_loading) return;

        var tag = item.Tag?.ToString();
        if (string.IsNullOrEmpty(tag))
        {
            _writer.Write(() => _editor.RemoveValue("frame-style"), "frame-style");
            return;
        }

        OnValueChanged("frame-style", tag);
    }

    private void NoColorOverride_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem item })
            OnValueChanged("no-color-override", item.Tag?.ToString() ?? "notify");
    }

    private void PowerSaverMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("power-saver-mode", item.Tag?.ToString() ?? "auto");
    }

    // Read from the file like the power-saver seed above: the card edits
    // what is written down, and an absent key is the card's default,
    // "system" (follow the Windows animation setting), not a value to
    // write back.
    private void SeedAnimationsCombo(ConfigService cs)
    {
        var animations = cs.GetRawFileValue("animations");
        if (string.IsNullOrWhiteSpace(animations)) animations = "system";
        SelectComboByTag(AnimationsCombo, animations.Trim().ToLowerInvariant());
    }

    private void Animations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("animations", item.Tag?.ToString() ?? "system");
    }

    private void BlurFollowsOpacity_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts)
            OnValueChanged("background-blur-follows-opacity", ts.IsOn ? "true" : "false");
    }

    private void TintColor_ColorChanged(object? sender, string hex)
    {
        OnValueChanged("background-tint-color", hex);
        TintColorResetButton.Visibility = Visibility.Visible;
    }

    private void TintColor_Reset(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _writer.Write(() => _editor.RemoveValue("background-tint-color"), "background-tint-color");

        _loading = true;
        try { TintColorPicker.Color = ""; }
        finally { _loading = false; }
        TintColorResetButton.Visibility = Visibility.Collapsed;
    }

    private void TintOpacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-tint-opacity", e.NewValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void LuminosityOpacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-luminosity-opacity", e.NewValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void GradientBlend_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("background-gradient-blend", item.Tag?.ToString() ?? "overlay");
    }

    private void GradientOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-gradient-opacity", e.NewValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void GradientSpeed_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-gradient-speed", e.NewValue.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void GradientEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = GradientEnabledToggle.IsOn;
        GradientSettingsPanel.Visibility = enabled
            ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled)
        {
            _writer.Write(
                () => _editor.RemoveValue("background-gradient-point"), "background-gradient-point");
            GradientEditor.SetPoints(System.Array.Empty<GradientPointModel>());
        }
        else if (GradientEditor.Points.Count == 0)
        {
            // Seed a default point when enabling for the first time.
            GradientEditor.SetPoints(new[]
            {
                new GradientPointModel(
                    0.5f, 0.5f, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x35), 0.5f),
            });
            WriteAllPoints();
        }
    }

    private void WriteAllPoints()
    {
        if (_loading) return;
        // The writer reloads after the (watcher-suppressed, IO-guarded)
        // write; that reload re-enters OnConfigChanged via the dispatcher,
        // where _expectingOwnReloads is decremented to skip the re-seed so
        // an in-progress picker flyout isn't torn down. The increment must
        // happen after the synchronous reload returns but before the
        // dispatched echo runs -- which is exactly here.
        var values = GradientEditor.Points
            .Select(p => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{p.X:0.###},{p.Y:0.###},#{p.Color.R:X2}{p.Color.G:X2}{p.Color.B:X2},{p.Radius:0.###}"))
            .ToArray();
        var result = _writer.Write(
            () => _editor.SetRepeatableValues("background-gradient-point", values),
            "background-gradient-point");
        if (result.Reloaded)
        {
            _expectingOwnReloads++;
        }
    }

    private void AnimationMode_Changed(object sender, object e)
    {
        if (_loading) return;
        var parts = new List<string>();

        // Position mode from radio buttons.
        if (PositionAnimRadio.SelectedItem is RadioButton rb)
        {
            var tag = rb.Tag?.ToString();
            if (!string.IsNullOrEmpty(tag)) parts.Add(tag);
        }

        if (BreatheCheck.IsChecked == true) parts.Add("breathe");
        if (ColorCycleCheck.IsChecked == true) parts.Add("color-cycle");

        var value = parts.Count > 0 ? string.Join(",", parts) : "static";
        OnValueChanged("background-gradient-animation", value);
    }

    private void OnConfigChanged(IConfigService svc)
    {
        // Echo from our own Reload(): the editor already reflects these
        // values, so skip the re-seed. That is also what keeps an in-progress
        // row -- a color picker flyout the user is dragging -- from being torn
        // down by the write the page just made.
        if (_expectingOwnReloads > 0)
        {
            _expectingOwnReloads--;
            return;
        }
        if (_loading) return;
        // GradientPoints and the other Windows-only values are on the concrete
        // ConfigService, not the interface. Bail silently for any other runtime
        // type (e.g. test fakes).
        if (svc is not ConfigService) return;

        // Everything on the page, not just the gradient editor and the shader
        // box. A control left showing the value the config had when the page
        // was built writes THAT value on the next nudge, so an external edit
        // -- the raw editor in this same dialog, a save from another editor,
        // the file watcher -- is silently undone the moment the user touches
        // a stale slider. SeedShaderPath also moves _shaderPathWritten with the
        // box, without which a re-commit of the displayed path would read as
        // unchanged and be suppressed.
        //
        // The guard is what stops this from being the same clobber by another
        // route: assigning a control fires its own handler, and each of those
        // writes the key.
        _loading = true;
        try
        {
            SeedFromConfig();
        }
        finally
        {
            _loading = false;
        }
    }

}
