using System;
using Ghostty.Core.Input;
using Ghostty.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Ghostty.Tabs;

internal sealed partial class NewTabSplitButton : UserControl
{
    private NewTabSplitButtonViewModel? _vm;
    private MainWindow? _owner;

    public NewTabSplitButton()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Owning window. Set by <see cref="TabHost"/> immediately after
    /// constructing the control so click handlers can call
    /// <see cref="MainWindow.OpenProfile"/>.
    /// </summary>
    internal MainWindow? Owner
    {
        get => _owner;
        set => _owner = value;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var registry = App.ProfileRegistry;
        if (registry is null) return;

        // WinUI 3 raises Loaded/Unloaded on every reparent. SwapChainPanel
        // rehost paths in this codebase have been observed to fire Loaded
        // a second time without an intervening Unloaded; defensively
        // dispose the prior VM and drop its registry subscription before
        // allocating a new one so the second Loaded does not leak.
        if (_vm is not null)
        {
            registry.ProfilesChanged -= OnProfilesChanged;
            _vm.Dispose();
            _vm = null;
        }

        _vm = new NewTabSplitButtonViewModel(registry);
        registry.ProfilesChanged += OnProfilesChanged;
        RebuildFlyout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var registry = App.ProfileRegistry;
        if (registry is not null)
            registry.ProfilesChanged -= OnProfilesChanged;
        _vm?.Dispose();
        _vm = null;
    }

    private void OnProfilesChanged(IProfileRegistry _) => RebuildFlyout();

    private void RebuildFlyout()
    {
        if (_vm is null) return;
        ProfileMenu.Items.Clear();

        foreach (var row in _vm.Rows)
        {
            var item = new MenuFlyoutItem
            {
                Text = row.IsDefault ? row.Name + "  *" : row.Name,
                Tag = row.Id,
            };
            AutomationProperties.SetName(item, row.Name);
            item.Click += OnRowClick;

            // Icon resolution is fire-and-forget; row keeps placeholder
            // until the resolve completes. PR 4 does not show a per-row
            // icon yet because MenuFlyoutItem.Icon expects an IconElement,
            // not a BitmapImage; full icon support lands when we wrap in
            // an Image inside a custom MenuFlyoutItem template (PR 6).

            ProfileMenu.Items.Add(item);
        }
    }

    private void OnPrimaryClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        var registry = App.ProfileRegistry;
        var defaultId = registry?.DefaultProfileId;
        if (defaultId is null || _owner is null) return;

        var modifiers = App.ModifierKeyState;
        if (modifiers is null) return;

        _owner.OpenProfile(defaultId, ClickModifierClassifier.Classify(modifiers));
    }

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        if (item.Tag is not string profileId) return;
        if (_owner is null) return;

        var modifiers = App.ModifierKeyState;
        if (modifiers is null) return;

        _owner.OpenProfile(profileId, ClickModifierClassifier.Classify(modifiers));
    }
}
