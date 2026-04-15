using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Ghostty.Controls.Settings;

/// <summary>
/// Atomic row of a settings page: bold label, optional muted
/// description, and a right-aligned control slot. Consumers put the
/// actual input control (ToggleSwitch, ComboBox, Slider, ColorPicker,
/// etc.) inside as child content.
///
/// Header and Description are exposed as dependency properties so
/// XAML consumers can write Header="Font size" without touching
/// code-behind. ConfigKey is reserved for Phase 3 (search overlay) --
/// it tags the card for scroll-to and pulse animation.
/// </summary>
[ContentProperty(Name = nameof(Control))]
public sealed partial class SettingsCard : UserControl
{
    public SettingsCard()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(SettingsCard),
            new PropertyMetadata(string.Empty, OnHeaderChanged));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingsCard)d;
        card.HeaderText.Text = (string)(e.NewValue ?? string.Empty);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(SettingsCard),
            new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingsCard)d;
        var text = (string)(e.NewValue ?? string.Empty);
        card.DescriptionText.Text = text;
        card.DescriptionText.Visibility =
            string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // Typed as UIElement (not object) so passing a plain string or a
    // view-model is a compile error instead of silently rendering as
    // text inside the ContentPresenter. XAML children still flow in
    // through the ContentProperty attribute.
    public static readonly DependencyProperty ControlProperty =
        DependencyProperty.Register(
            nameof(Control),
            typeof(UIElement),
            typeof(SettingsCard),
            new PropertyMetadata(null, OnControlChanged));

    public UIElement? Control
    {
        get => (UIElement?)GetValue(ControlProperty);
        set => SetValue(ControlProperty, value);
    }

    private static void OnControlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (SettingsCard)d;
        card.ControlPresenter.Content = e.NewValue;
    }

    // Reserved for Phase 3: search scroll + pulse animation targets
    // this attached property to find the card for a given config key.
    public static readonly DependencyProperty ConfigKeyProperty =
        DependencyProperty.RegisterAttached(
            "ConfigKey",
            typeof(string),
            typeof(SettingsCard),
            new PropertyMetadata(string.Empty));

    public static string GetConfigKey(DependencyObject obj) =>
        (string)obj.GetValue(ConfigKeyProperty);

    public static void SetConfigKey(DependencyObject obj, string value) =>
        obj.SetValue(ConfigKeyProperty, value);
}
