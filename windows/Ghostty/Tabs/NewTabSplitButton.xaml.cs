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

    public NewTabSplitButton()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Owning window. Set by <see cref="TabHost"/> or
    /// <see cref="VerticalTabStrip"/> immediately after constructing
    /// the control so click handlers can call
    /// <see cref="MainWindow.OpenProfile"/>.
    /// </summary>
    internal MainWindow? Owner { get; set; }

    /// <summary>
    /// Placement for the profile dropdown flyout. Default
    /// <see cref="FlyoutPlacementMode.Bottom"/> matches the horizontal
    /// tab-strip footer; the vertical-strip footer sets it to
    /// <see cref="FlyoutPlacementMode.Right"/> so the menu opens away
    /// from the sidebar instead of off the bottom edge of the window.
    /// </summary>
    public FlyoutPlacementMode FlyoutPlacement
    {
        get => (FlyoutPlacementMode)GetValue(FlyoutPlacementProperty);
        set => SetValue(FlyoutPlacementProperty, value);
    }

    public static readonly DependencyProperty FlyoutPlacementProperty =
        DependencyProperty.Register(
            nameof(FlyoutPlacement),
            typeof(FlyoutPlacementMode),
            typeof(NewTabSplitButton),
            new PropertyMetadata(FlyoutPlacementMode.Bottom, OnFlyoutPlacementChanged));

    private static void OnFlyoutPlacementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // ProfileMenu is created during InitializeComponent; the DP
        // can be set before parent-XAML attachment in some hosting
        // paths, so guard the field access defensively.
        if (d is NewTabSplitButton ctl && ctl.ProfileMenu is not null)
            ctl.ProfileMenu.Placement = (FlyoutPlacementMode)e.NewValue;
    }

    /// <summary>
    /// Stack direction for the primary "+" button and its chevron
    /// dropdown. Horizontal (default) places the chevron to the right
    /// of the primary action, matching the WinUI SplitButton it
    /// replaces. Vertical stacks the chevron below the primary so the
    /// control fits in a narrow vertical-strip footer without forcing
    /// the strip column to widen.
    /// </summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(
            nameof(Orientation),
            typeof(Orientation),
            typeof(NewTabSplitButton),
            new PropertyMetadata(Orientation.Horizontal, OnOrientationChanged));

    private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NewTabSplitButton ctl && ctl.LayoutRoot is not null)
            ctl.LayoutRoot.Orientation = (Orientation)e.NewValue;
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

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        var registry = App.ProfileRegistry;
        var defaultId = registry?.DefaultProfileId;
        if (defaultId is null || Owner is null) return;

        var modifiers = App.ModifierKeyState;
        if (modifiers is null) return;

        Owner.OpenProfile(defaultId, ClickModifierClassifier.Classify(modifiers));
    }

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;
        if (item.Tag is not string profileId) return;
        if (Owner is null) return;

        var modifiers = App.ModifierKeyState;
        if (modifiers is null) return;

        Owner.OpenProfile(profileId, ClickModifierClassifier.Classify(modifiers));
    }
}
