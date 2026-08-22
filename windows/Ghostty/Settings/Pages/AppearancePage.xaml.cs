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
        // without the ctor (or any seeding) running again. Recreate whatever
        // the preview showed; Page_Unloaded disposed it on the way out.
        UpdateShaderPreview();
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
        SelectShaderComboForPath(ShaderPathBox.Text);
        UpdateShaderPreview();
    }

    // ── Shader gallery ─────────────────────────────────────────────────────

    // Gallery entries keyed by the absolute installed path of their shader
    // file, so a configured path can be mapped back to its combo item.
    private readonly Dictionary<string, ShaderGalleryEntry> _shaderGalleryByPath = new();

    private void PopulateShaderGallery()
    {
        if (ShaderGallery.Entries.Count == 0 && ShaderGallery.LoadDetail is { } detail)
        {
            StaticLoggers.SettingsConfigWriter.LogInformation(
                "shader gallery empty: {Detail} (base: {Base})",
                detail, AppContext.BaseDirectory);
        }
        var items = ShaderGalleryCombo.Items;
        // -1 would make Insert below throw and take the whole page down;
        // appending at the end is the correct degenerate order.
        var customIndex = items.IndexOf(ShaderCustomFileItem);
        if (customIndex < 0) customIndex = items.Count;
        foreach (var entry in ShaderGallery.Entries)
        {
            _shaderGalleryByPath[ShaderGallery.AbsolutePathFor(entry)] = entry;
            var item = new ComboBoxItem { Tag = ShaderGallery.AbsolutePathFor(entry) };
            var panel = new StackPanel();
            // Explicit typography rather than style/theme-dictionary lookups:
            // programmatic theme-resource fetches are unreliable in WinUI 3
            // desktop, and the two-line shape matches the XAML items.
            panel.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            panel.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            item.Content = panel;
            // Before the "Custom file" item, keeping None first.
            items.Insert(customIndex++, item);
        }
    }

    private void SelectShaderComboForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SelectComboByTag(ShaderGalleryCombo, "");
        }
        else if (_shaderGalleryByPath.TryGetValue(path, out _))
        {
            SelectComboByTag(ShaderGalleryCombo, path);
        }
        else
        {
            SelectComboByTag(ShaderGalleryCombo, "custom");
        }
    }

    private void ShaderGallery_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ComboBox combo) return;
        if (combo.SelectedItem is not ComboBoxItem item) return;

        var tag = item.Tag?.ToString();
        if (tag == "custom")
        {
            // No write of its own: the path box below is the source of truth
            // for a custom file. Preview whatever it holds.
            UpdateShaderPreview(ShaderPathBox.Text);
            return;
        }

        // None ("" or null) clears; a gallery path writes that one shader.
        var value = string.IsNullOrEmpty(tag) ? null : tag;
        WriteShaderPathValue(value ?? string.Empty);

        // Keep the two controls telling the same story.
        ShaderPathBox.Text = value ?? string.Empty;
        UpdateShaderPreview(value);
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
            SelectShaderComboForPath(file.Path);
            UpdateShaderPreview(file.Path);
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

    private Controls.TerminalControl? _shaderPreview;

    /// <summary>
    /// Recreates the preview surface with the given shader applied (null or
    /// empty = plain terminal). The per-surface override flows through
    /// TerminalControl.PreviewCustomShader, so browsing the gallery never
    /// touches the app config or any live terminal.
    /// </summary>
    private void UpdateShaderPreview(string? shaderPath = null)
    {
        if (ShaderPreviewHost is null) return;

        // Dispose the old preview's surface explicitly. Detaching from the
        // tree does NOT tear it down (TerminalControl.OnUnloaded is
        // deliberately a no-op there), and each preview owns a native
        // surface and a shell process rooted in the shared bootstrap host.
        _shaderPreview?.DisposeSurface();
        _shaderPreview = null;
        ShaderPreviewHost.Child = null;

        var host = App.BootstrapHost;
        if (host is null) return;

        // The path box is the source of truth for a custom file; the combo's
        // "custom" Tag is a discriminator, never a shader path (passing it
        // through would arm the shader-failed notice for a valid file).
        var path = string.IsNullOrWhiteSpace(shaderPath)
            ? ShaderPathBox.Text
            : shaderPath;
        if (string.IsNullOrEmpty(path)) path = null;

        var control = new Controls.TerminalControl
        {
            Host = host,
            PreviewCustomShader = path,
        };
        _shaderPreview = control;
        ShaderPreviewHost.Child = control;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // Dispose the preview surface (and its shell) when leaving the page;
        // OnLoaded recreates it on return. Detaching alone leaks both.
        _shaderPreview?.DisposeSurface();
        _shaderPreview = null;
        ShaderPreviewHost.Child = null;
    }

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
        SelectShaderComboForPath(value);
        UpdateShaderPreview(value);
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
