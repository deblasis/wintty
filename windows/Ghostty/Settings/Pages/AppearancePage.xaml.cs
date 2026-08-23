using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Controls.Settings;
using Ghostty.Core.Settings;
using Ghostty.Core.Config;
using Ghostty.Core.DirectWrite;
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
        OpacitySlider.Value = configService.BackgroundOpacity;
        SelectWindowTheme(configService.WindowTheme);

        // Seed font size from current config before the loading guard
        // flips off so the ValueChanged handler doesn't fire a redundant
        // write back to disk.
        if (configService is ConfigService csFont)
        {
            FontSizeBox.Value = csFont.FontSize;
        }

        // Windows-only properties are on the concrete ConfigService, not IConfigService.
        // Cast to read current values for initialization; fall back to defaults if the
        // runtime type is different (e.g. in tests).
        if (configService is ConfigService cs)
        {
            SelectComboByTag(BackgroundStyleCombo, cs.BackgroundStyle);

            // Seed power saver mode from config, defaulting to "auto".
            var powerMode = cs.GetRawFileValue("power-saver-mode");
            if (string.IsNullOrWhiteSpace(powerMode)) powerMode = "auto";
            SelectComboByTag(PowerSaverModeCombo, powerMode.Trim().ToLowerInvariant());

            // NoColorOverride is already normalized to one of notify/strip/keep.
            SelectComboByTag(NoColorOverrideCombo, cs.NoColorOverride);

            SeedShaderPath();

            BlurFollowsOpacityToggle.IsOn = cs.BackgroundBlurFollowsOpacity;
            if (cs.IsConfiguredInFile("background-tint-color"))
            {
                if (cs.BackgroundTintColor.HasValue)
                {
                    var c = cs.BackgroundTintColor.Value;
                    TintColorPicker.Color = new Rgb(c.R, c.G, c.B).ToHex();
                }
                TintColorResetButton.Visibility = Visibility.Visible;
            }
            else
            {
                TintColorPicker.Color = "";
                TintColorResetButton.Visibility = Visibility.Collapsed;
            }
            TintOpacitySlider.Value = cs.BackgroundTintOpacity ?? 0.3;
            LuminosityOpacitySlider.Value = cs.BackgroundLuminosityOpacity ?? 0.3;
        }
        else
        {
            SelectComboByTag(BackgroundStyleCombo, "frosted");
        }

        // Initialize gradient settings from current config.
        if (configService is ConfigService configSvc)
        {
            var points = configSvc.GradientPoints;
            GradientEnabledToggle.IsOn = points.Count > 0;
            GradientSettingsPanel.Visibility = points.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;

            // Load existing points into editor.
            GradientEditor.SetPoints(points
                .Select(p => new GradientPointModel(p.X, p.Y, p.Color, p.Radius))
                .ToList());

            // Parse animation mode into radio + checkboxes.
            var anim = configSvc.GradientAnimation;
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

            GradientSpeedSlider.Value = configSvc.GradientSpeed;
            GradientOpacitySlider.Value = configSvc.GradientOpacity;

            SelectComboByTag(GradientBlendCombo, configSvc.GradientBlend);
        }

        GradientEditor.PointsChanged += (_, _) => WriteAllPoints();

        _loading = false;

        // Re-seed the gradient editor when the config file changes on disk.
        // The editor's own writes set _loading/SuppressWatcher, so this only
        // fires for genuine external edits. Subscribe in Loaded rather than the
        // ctor: SettingsWindow caches and reuses page instances, so the ctor
        // runs once while Loaded/Unloaded fire on every navigation. A ctor-time
        // subscription paired with an Unloaded unsubscribe would be dropped the
        // first time the user navigates away and never restored on return.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        LoadFontsAsync();
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
        // libghostty still accepts the pre-rename "ghostty" spelling and hands
        // it back verbatim, but the combo only carries the preferred "wintty"
        // tag. Fold the alias here so an unmigrated config selects the right
        // item instead of falling through to "auto" below.
        var wanted = string.Equals(theme, "ghostty", StringComparison.OrdinalIgnoreCase)
            ? "wintty"
            : theme;

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

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
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
        var owner = App.SettingsWindow;
        void OnOwnerClosed(object? s, RoutedEventArgs e) => picker.Close();
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
            OnValueChanged("background-style", item.Tag?.ToString() ?? "frosted");
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
        // Echo from our own Reload(): editor already reflects these
        // values, so skip the rebuild (which would tear down any open
        // row, like a color picker flyout the user is dragging).
        if (_expectingOwnReloads > 0)
        {
            _expectingOwnReloads--;
            return;
        }
        if (_loading) return;
        // GradientPoints is on the concrete ConfigService, not the interface.
        // Bail silently for any other runtime type (e.g. test fakes).
        if (svc is not ConfigService cs) return;
        _loading = true;
        try
        {
            GradientEditor.SetPoints(cs.GradientPoints
                .Select(p => new Controls.Settings.GradientPointModel(
                    p.X, p.Y, p.Color, p.Radius))
                .ToList());

            // The shader box has to move with the file too. Leaving it alone
            // would not merely show a stale value: _shaderPathWritten would go
            // on describing a write this page made before the external edit
            // undid it, so re-committing the displayed value would read as
            // unchanged and be suppressed.
            SeedShaderPath();
        }
        finally
        {
            _loading = false;
        }
    }

}
