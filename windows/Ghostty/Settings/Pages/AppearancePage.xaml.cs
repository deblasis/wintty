using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Config;
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

        // Windows-only properties are on the concrete ConfigService, not IConfigService.
        // Cast to read current values for initialization; fall back to defaults if the
        // runtime type is different (e.g. in tests).
        if (configService is ConfigService cs)
        {
            ShellThemeToggle.IsOn = cs.ShellThemeEnabled;
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
            });
        });
    }

    private static unsafe List<string> EnumerateSystemFonts()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        IntPtr factory;
        if (DWriteCreateFactory(0, &iid, &factory) != 0 || factory == IntPtr.Zero)
            return new List<string>();

        try
        {
            // IDWriteFactory: IUnknown(3) + GetSystemFontCollection(3)
            // checkForUpdates=1 to include per-user installed fonts.
            var vtable = (IntPtr*)*(IntPtr*)factory;
            IntPtr collection;
            var getCollection = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int, int>)vtable[3];
            if (getCollection(factory, &collection, 1) != 0 || collection == IntPtr.Zero)
                return new List<string>();

            try
            {
                var cvt = (IntPtr*)*(IntPtr*)collection;
                // IDWriteFontCollection: GetFontFamilyCount(3), GetFontFamily(4)
                // (verified against src/font/directwrite.zig:585)
                var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint>)cvt[3];
                var getFamily = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)cvt[4];
                var count = getCount(collection);

                for (uint i = 0; i < count; i++)
                {
                    IntPtr familyPtr;
                    if (getFamily(collection, i, &familyPtr) != 0 || familyPtr == IntPtr.Zero)
                        continue;
                    try
                    {
                        var name = GetFamilyName(familyPtr);
                        if (name != null) families.Add(name);
                    }
                    finally
                    {
                        Marshal.Release(familyPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(collection);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        // Ghostty embeds JetBrains Mono in the binary so it's always
        // available even if not installed on the system.
        families.Add("JetBrains Mono");

        return families.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static unsafe string? GetFamilyName(IntPtr familyPtr)
    {
        var fvt = (IntPtr*)*(IntPtr*)familyPtr;
        // IDWriteFontFamily: IUnknown(3) + IDWriteFontList(3) + GetFamilyNames(6)
        // (verified against src/font/directwrite.zig:549)
        IntPtr namesPtr;
        var getNames = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)fvt[6];
        if (getNames(familyPtr, &namesPtr) != 0 || namesPtr == IntPtr.Zero)
            return null;

        try
        {
            var nvt = (IntPtr*)*(IntPtr*)namesPtr;
            // IDWriteLocalizedStrings: GetCount(3), GetStringLength(7), GetString(8)
            // (verified against src/font/directwrite.zig:120)
            var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint>)nvt[3];
            if (getCount(namesPtr) == 0) return null;

            uint len;
            var getLen = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)nvt[7];
            if (getLen(namesPtr, 0, &len) != 0) return null;

            var buf = stackalloc char[(int)len + 1];
            var getString = (delegate* unmanaged[Stdcall]<IntPtr, uint, char*, uint, int>)nvt[8];
            if (getString(namesPtr, 0, buf, len + 1) != 0) return null;

            return new string(buf, 0, (int)len);
        }
        finally
        {
            Marshal.Release(namesPtr);
        }
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

    private void ShellTheme_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts)
            OnValueChanged("windows-shell-theme", ts.IsOn ? "true" : "false");
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

    [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe partial int DWriteCreateFactory(int factoryType, Guid* iid, IntPtr* factory);

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
