using System;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Title-bar pill control for update status. Uses code-behind binding
/// (same pattern as <c>CommandPaletteControl</c> / <c>UpdatePopover</c>):
/// internal VM wired via <see cref="Bind"/>, which subscribes to
/// PropertyChanged and refreshes element properties on each transition.
/// The child <c>UpdatePopover</c> receives its own VM through its
/// own <c>Bind</c> method.
/// </summary>
internal sealed partial class UpdatePill : UserControl
{
    private UpdatePillViewModel? _vm;
    private UpdatePopoverViewModel? _popoverVm;

    public UpdatePill()
    {
        InitializeComponent();
    }

    public void Bind(UpdatePillViewModel pillVm, UpdatePopoverViewModel popoverVm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        if (_popoverVm is not null)
        {
            _popoverVm.CloseRequested -= OnPopoverCloseRequested;
        }

        _vm = pillVm;
        _popoverVm = popoverVm;
        // Click+CanExecuteChanged instead of Button.Command: WinUI 3 +
        // CsWinRT needs a CCW table entry for the concrete command type,
        // which isn't emitted for managed ICommand impls assigned in
        // code-behind (only for XAML-bound {Binding}/x:Bind). Click is
        // equivalent for our one-way invocation and keeps the Mvvm
        // primitives assembly-internal.
        WireCommand(PillButton, pillVm.TogglePopoverCommand);
        PillPopover.Bind(popoverVm);

        pillVm.PropertyChanged += OnVmPropertyChanged;
        popoverVm.CloseRequested += OnPopoverCloseRequested;
        Refresh();
    }

    private void OnPopoverCloseRequested(object? sender, EventArgs e)
    {
        PillButton.Flyout?.Hide();
    }

    private static void WireCommand(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button, ICommand command)
    {
        button.Click += (_, _) =>
        {
            if (command.CanExecute(null)) command.Execute(null);
        };
        button.IsEnabled = command.CanExecute(null);
        command.CanExecuteChanged += (_, _) => button.IsEnabled = command.CanExecute(null);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_vm is null) return;

        Visibility = _vm.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        PillLabel.Text = _vm.Label;
        PillIcon.Glyph = _vm.IconGlyph;
        PillIcon.Visibility = _vm.ShowProgressRing ? Visibility.Collapsed : Visibility.Visible;
        PillProgress.Visibility = _vm.ShowProgressRing ? Visibility.Visible : Visibility.Collapsed;
        PillProgress.IsIndeterminate = _vm.IsIndeterminate;
        PillProgress.Value = _vm.ProgressValue;
    }
}
