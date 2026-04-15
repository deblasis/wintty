using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System.Collections.ObjectModel;

namespace Ghostty.Controls.Settings;

/// <summary>
/// Section heading with a vertical list of settings cards.
/// Header and Description are dependency properties; child cards
/// are the Content (Items) of an inner ItemsControl.
///
/// Matches the Section field of <see cref="Ghostty.Core.Settings.SettingsEntry"/>
/// so the page layout mirrors the index exactly.
/// </summary>
[ContentProperty(Name = nameof(Cards))]
public sealed partial class SettingsGroup : UserControl
{
    public SettingsGroup()
    {
        InitializeComponent();
        Cards = new ObservableCollection<object>();
        CardsPresenter.ItemsSource = Cards;
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(SettingsGroup),
            new PropertyMetadata(string.Empty, OnHeaderChanged));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (SettingsGroup)d;
        g.HeaderText.Text = (string)(e.NewValue ?? string.Empty);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(SettingsGroup),
            new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (SettingsGroup)d;
        var text = (string)(e.NewValue ?? string.Empty);
        g.DescriptionText.Text = text;
        g.DescriptionText.Visibility =
            string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // Observable so adding cards at runtime repaints. Typed as object
    // rather than SettingsCard so a Task (future) could embed a
    // custom control directly without wrapping it.
    public ObservableCollection<object> Cards { get; }
}
