using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Core.Config;
using Ghostty.Core.DirectWrite;
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
    private readonly List<GradientPointEditor> _pointEditors = [];

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
                TintColorBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
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

            // Load existing points into editors.
            foreach (var pt in points)
            {
                AddPointEditor(pt.X, pt.Y,
                    $"#{pt.Color.R:X2}{pt.Color.G:X2}{pt.Color.B:X2}", pt.Radius);
            }

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

        UpdateAddButtonVisibility();
        _loading = false;
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
        _editor.SetValue(key, value);
        _configService.SuppressWatcher(false);
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

    private void TintColor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) OnValueChanged("background-tint-color", tb.Text);
    }

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
            _editor.RemoveValue("background-gradient-point");
            _configService.SuppressWatcher(false);
            _configService.Reload();
            _pointEditors.Clear();
            PointsPanel.Children.Clear();
        }
        else if (_pointEditors.Count == 0)
        {
            // Add a default point when enabling.
            AddPointEditor(0.5f, 0.5f, "#FF6B35", 0.5f);
            WriteAllPoints();
        }
    }

    private void AddPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_pointEditors.Count >= 5) return;
        AddPointEditor(0.5f, 0.5f, "#F7C948", 0.4f);
        WriteAllPoints();
        UpdateAddButtonVisibility();
    }

    private void AddPointEditor(float x, float y, string color, float radius)
    {
        var editor = new GradientPointEditor(
            _pointEditors.Count,
            () => { if (!_loading) WriteAllPoints(); },
            RemovePointEditor);
        editor.XSlider.Value = x;
        editor.YSlider.Value = y;
        editor.ColorBox.Text = color;
        editor.RadiusSlider.Value = radius;
        _pointEditors.Add(editor);
        PointsPanel.Children.Add(editor.Panel);
        UpdateAddButtonVisibility();
    }

    private void RemovePointEditor(GradientPointEditor editor)
    {
        _pointEditors.Remove(editor);
        PointsPanel.Children.Remove(editor.Panel);
        // Renumber remaining points.
        for (int i = 0; i < _pointEditors.Count; i++)
        {
            var header = _pointEditors[i].Panel.Children[0] as StackPanel;
            if (header?.Children[0] is TextBlock tb)
                tb.Text = $"Point {i + 1}";
        }
        WriteAllPoints();
        UpdateAddButtonVisibility();

        if (_pointEditors.Count == 0)
        {
            GradientEnabledToggle.IsOn = false;
            GradientSettingsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void WriteAllPoints()
    {
        if (_loading) return;
        var values = _pointEditors.Select(e => e.ToConfigValue()).ToArray();
        _configService.SuppressWatcher(true);
        _editor.SetRepeatableValues("background-gradient-point", values);
        _configService.SuppressWatcher(false);
        _configService.Reload();
    }

    private void UpdateAddButtonVisibility()
    {
        AddPointButton.IsEnabled = _pointEditors.Count < 5;
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

    private sealed class GradientPointEditor
    {
        public Slider XSlider { get; }
        public Slider YSlider { get; }
        public TextBox ColorBox { get; }
        public Slider RadiusSlider { get; }
        public Button RemoveButton { get; }
        public StackPanel Panel { get; }

        public GradientPointEditor(int index, Action onChanged, Action<GradientPointEditor> onRemove)
        {
            Panel = new StackPanel
            {
                Spacing = 4,
                Padding = new Thickness(8),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = $"Point {index + 1}",
                VerticalAlignment = VerticalAlignment.Center
            });
            RemoveButton = new Button { Content = "Remove", Padding = new Thickness(4, 2, 4, 2) };
            RemoveButton.Click += (_, _) => onRemove(this);
            header.Children.Add(RemoveButton);
            Panel.Children.Add(header);

            XSlider = new Slider
            {
                Header = "X position",
                Minimum = 0,
                Maximum = 1,
                StepFrequency = 0.05,
                Value = 0.5
            };
            XSlider.ValueChanged += (_, _) => onChanged();
            Panel.Children.Add(XSlider);

            YSlider = new Slider
            {
                Header = "Y position",
                Minimum = 0,
                Maximum = 1,
                StepFrequency = 0.05,
                Value = 0.5
            };
            YSlider.ValueChanged += (_, _) => onChanged();
            Panel.Children.Add(YSlider);

            ColorBox = new TextBox { Header = "Color", PlaceholderText = "#RRGGBB", Text = "#FF6B35" };
            ColorBox.LostFocus += (_, _) => onChanged();
            Panel.Children.Add(ColorBox);

            RadiusSlider = new Slider
            {
                Header = "Radius",
                Minimum = 0.1,
                Maximum = 1,
                StepFrequency = 0.05,
                Value = 0.5
            };
            RadiusSlider.ValueChanged += (_, _) => onChanged();
            Panel.Children.Add(RadiusSlider);
        }

        public string ToConfigValue()
        {
            var color = ColorBox.Text.Trim();
            if (!color.StartsWith('#')) color = "#" + color;
            return $"{XSlider.Value:F2},{YSlider.Value:F2},{color},{RadiusSlider.Value:F2}";
        }
    }
}
