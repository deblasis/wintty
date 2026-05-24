using System;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings;

public sealed partial class ProfileIconPickerControl : UserControl
{
    public event EventHandler<IconSpec?>? IconChanged;

    public static readonly DependencyProperty CurrentIconProperty =
        DependencyProperty.Register(
            nameof(CurrentIcon), typeof(IconSpec), typeof(ProfileIconPickerControl),
            new PropertyMetadata(null, OnCurrentIconChanged));

    public IconSpec? CurrentIcon
    {
        get => (IconSpec?)GetValue(CurrentIconProperty);
        set => SetValue(CurrentIconProperty, value);
    }

    public ProfileIconPickerControl()
    {
        InitializeComponent();
    }

    private static void OnCurrentIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProfileIconPickerControl ctrl) return;
        if (e.NewValue is IconSpec spec)
        {
            ctrl.PreviewHost.Content = new TabIconViewModel(spec, ctrl.LabelForSpec(spec));
            ctrl.SpecLabel.Text = ctrl.LabelForSpec(spec);
        }
        else
        {
            ctrl.PreviewHost.Content = null;
            ctrl.SpecLabel.Text = "(default)";
        }
    }

    private string LabelForSpec(IconSpec spec) => spec switch
    {
        IconSpec.BrandKey b => $"brand:{b.Key}",
        IconSpec.BundledKey b => $"bundled:{b.Key}",
        IconSpec.Mdl2Token m => $"mdl2:{m.CodePoint:X4}",
        IconSpec.Path p => p.FilePath,
        IconSpec.AutoForExe => "auto (exe)",
        IconSpec.AutoForWslDistro => "auto (wsl)",
        _ => "default",
    };

    private async void OnChangeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new IconPickerDialog
        {
            XamlRoot = this.XamlRoot,
            InitialSpec = CurrentIcon,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.PickedSpec is { } picked)
        {
            CurrentIcon = picked;
            IconChanged?.Invoke(this, picked);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        CurrentIcon = null;
        IconChanged?.Invoke(this, null);
    }
}
