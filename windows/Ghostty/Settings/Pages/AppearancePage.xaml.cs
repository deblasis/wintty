using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Controls.Settings;
using Ghostty.Core.Config;
using Ghostty.Core.DirectWrite;
using Ghostty.Core.Settings;
using Ghostty.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Ghostty.Settings.Pages;

internal sealed partial class AppearancePage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
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
        InitializeComponent();
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
            BlurFollowsOpacityToggle.IsOn = cs.BackgroundBlurFollowsOpacity;
            if (cs.BackgroundTintColor.HasValue)
            {
                var c = cs.BackgroundTintColor.Value;
                TintColorPicker.Color = new Rgb(c.R, c.G, c.B).ToHex();
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
        // fires for genuine external edits.
        _configService.ConfigChanged += OnConfigChanged;
        Unloaded += (_, _) => _configService.ConfigChanged -= OnConfigChanged;

        LoadFontsAsync();
    }

    private void SelectWindowTheme(string theme)
    {
        foreach (ComboBoxItem item in WindowThemeCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), theme, StringComparison.OrdinalIgnoreCase))
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
        _configService.SuppressWatcher(true);
        try { _editor.SetValue(key, value); }
        finally { _configService.SuppressWatcher(false); }
        _configService.Reload();
    }

    private void FontSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        OnValueChanged("font-size", sender.Value.ToString());
    }

    private void Opacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-opacity", e.NewValue.ToString("F2"));
    }

    private void WindowTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("window-theme", item.Tag?.ToString() ?? "auto");
    }

    private void ShaderPath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) OnValueChanged("custom-shader", tb.Text);
    }

    private void BackgroundStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("background-style", item.Tag?.ToString() ?? "frosted");
    }

    private void BlurFollowsOpacity_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts)
            OnValueChanged("background-blur-follows-opacity", ts.IsOn ? "true" : "false");
    }

    private void TintColor_ColorChanged(object? sender, string hex)
        => OnValueChanged("background-tint-color", hex);

    private void TintOpacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-tint-opacity", e.NewValue.ToString("F2"));
    }

    private void LuminosityOpacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-luminosity-opacity", e.NewValue.ToString("F2"));
    }

    private void GradientBlend_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("background-gradient-blend", item.Tag?.ToString() ?? "overlay");
    }

    private void GradientOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-gradient-opacity", e.NewValue.ToString("F2"));
    }

    private void GradientSpeed_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-gradient-speed", e.NewValue.ToString("F1"));
    }

    private void GradientEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = GradientEnabledToggle.IsOn;
        GradientSettingsPanel.Visibility = enabled
            ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled)
        {
            _configService.SuppressWatcher(true);
            try { _editor.RemoveValue("background-gradient-point"); }
            finally { _configService.SuppressWatcher(false); }
            _configService.Reload();
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
        // Reload() fires ConfigChanged synchronously; OnConfigChanged
        // decrements _expectingOwnReloads and skips the re-seed so an
        // in-progress picker flyout isn't torn down. Inner try/finally
        // on SuppressWatcher keeps the watcher flag balanced if
        // SetRepeatableValues throws.
        var values = GradientEditor.Points
            .Select(p => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{p.X:0.###},{p.Y:0.###},#{p.Color.R:X2}{p.Color.G:X2}{p.Color.B:X2},{p.Radius:0.###}"))
            .ToArray();
        _configService.SuppressWatcher(true);
        try { _editor.SetRepeatableValues("background-gradient-point", values); }
        finally { _configService.SuppressWatcher(false); }
        if (_configService.Reload())
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
        }
        finally
        {
            _loading = false;
        }
    }

}
