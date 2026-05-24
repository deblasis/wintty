using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ghostty.Controls.Settings;
using Ghostty.Core.Config;
using Ghostty.Core.Profiles;
using Ghostty.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

/// <summary>
/// Read-only listing of the visible profiles plus the resolved
/// default-profile id, with a per-row toggle that flips the profile's
/// hidden state. Re-binds whenever the registry recomposes (config
/// reload or background discovery refresh). Hidden expander and
/// parser-warning surface land in subsequent commits.
/// </summary>
internal sealed partial class ProfilesPage : Page
{
    private readonly IProfileRegistry _registry;
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;

    // Per-id row cache so a Rebind triggered by a background event
    // (e.g. discovery completion) updates existing SettingsCard instances
    // in place rather than tearing down the whole list. Preserves the
    // ToggleSwitch identity of any row the user happens to be touching.
    private readonly Dictionary<string, SettingsCard> _cardsByProfileId =
        new(StringComparer.OrdinalIgnoreCase);

    // Set while Rebind is mutating ToggleSwitch state programmatically
    // so OnHiddenToggled doesn't see the synthetic Toggled event and
    // try to write back to config. Mirrors AppearancePage's _loading
    // pattern for the same reason.
    private bool _loading;

    public ProfilesPage(
        IProfileRegistry registry,
        IConfigService configService,
        IConfigFileEditor editor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(editor);
        _registry = registry;
        _configService = configService;
        _editor = editor;
        InitializeComponent();

        // Subscribe at Loaded / unsubscribe at Unloaded so a cached
        // Page that gets navigated away from doesn't keep holding the
        // registry's event source alive past its useful lifetime.
        Loaded += (_, _) =>
        {
            Rebind();
            _registry.ProfilesChanged += OnProfilesChanged;
        };
        Unloaded += (_, _) => _registry.ProfilesChanged -= OnProfilesChanged;
    }

    // ProfilesChanged is raised on the UI dispatcher per
    // IProfileRegistry's contract, but TryEnqueue keeps Rebind safe if
    // a future implementation ever fires synchronously from a
    // background thread.
    private void OnProfilesChanged(IProfileRegistry _) =>
        DispatcherQueue.TryEnqueue(Rebind);

    private void Rebind()
    {
        _loading = true;
        try
        {
            var warnings = _configService.ProfileWarnings;
            if (warnings.Count == 0)
            {
                WarningsBar.IsOpen = false;
                WarningsBar.Message = string.Empty;
            }
            else
            {
                WarningsBar.Message = string.Join("\n", warnings);
                WarningsBar.IsOpen = true;
            }

            DefaultProfileCard.Description =
                _registry.DefaultProfileId ?? "(no default profile set)";

            // Build the desired ordered list (visible first, then hidden)
            // and upsert into ProfilesGroup.Cards in place. Visible and
            // hidden share one flat list so the user can flip a profile's
            // hidden state without losing it off-screen; hidden profiles
            // still get filtered out of the new-tab menu / palette /
            // chords because those consumers read _registry.Profiles.
            var desired = new List<(ResolvedProfile Profile, bool IsHidden)>(
                _registry.Profiles.Count + _registry.HiddenProfiles.Count);
            foreach (var p in _registry.Profiles) desired.Add((p, false));
            foreach (var p in _registry.HiddenProfiles) desired.Add((p, true));

            // Drop cards whose id is no longer in the registry.
            var desiredIds = new HashSet<string>(desired.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (p, _) in desired) desiredIds.Add(p.Id);
            var stale = _cardsByProfileId.Keys.Where(k => !desiredIds.Contains(k)).ToList();
            foreach (var id in stale)
            {
                var card = _cardsByProfileId[id];
                if (FindToggle(card) is ToggleSwitch t) t.Toggled -= OnHiddenToggled;
                if (FindPicker(card) is ProfileIconPickerControl picker)
                    picker.IconChanged -= OnProfileIconChanged;
                ProfilesGroup.Cards.Remove(card);
                _cardsByProfileId.Remove(id);
            }

            // Place each desired card at the correct index, creating new
            // cards as needed and reordering existing ones via Move so
            // the visual tree of an unaffected row survives the rebind.
            for (int i = 0; i < desired.Count; i++)
            {
                var (profile, isHidden) = desired[i];
                if (_cardsByProfileId.TryGetValue(profile.Id, out var card))
                {
                    card.Header = profile.Name;
                    card.Description = profile.Command;
                    if (FindToggle(card) is ToggleSwitch toggle && toggle.IsOn != isHidden)
                        toggle.IsOn = isHidden;
                    if (FindPicker(card) is ProfileIconPickerControl picker)
                        picker.CurrentIcon = profile.Icon;

                    var currentIdx = ProfilesGroup.Cards.IndexOf(card);
                    if (currentIdx != i) ProfilesGroup.Cards.Move(currentIdx, i);
                }
                else
                {
                    card = BuildRow(profile, isHidden);
                    _cardsByProfileId[profile.Id] = card;
                    ProfilesGroup.Cards.Insert(i, card);
                }
            }
        }
        finally { _loading = false; }
    }

    // Builds one SettingsCard with the icon picker + hidden toggle on
    // the right. SettingsCard.Control is a single UIElement slot, so we
    // wrap both into a horizontal StackPanel. Tag on the toggle carries
    // the profile id; the picker resolves its id via the shared
    // StackPanel.Tag walked through FindPicker / FindToggle in Rebind.
    private SettingsCard BuildRow(ResolvedProfile profile, bool isHidden)
    {
        var picker = new ProfileIconPickerControl
        {
            CurrentIcon = profile.Icon,
        };
        picker.IconChanged += OnProfileIconChanged;

        var toggle = new ToggleSwitch
        {
            IsOn = isHidden,
            OffContent = "Visible",
            OnContent = "Hidden",
            Tag = profile.Id,
        };
        toggle.Toggled += OnHiddenToggled;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            // Stash the profile id on the panel so the picker handler
            // can recover it from the picker's parent without a per-row
            // closure capture (matches the toggle's Tag pattern).
            Tag = profile.Id,
        };
        panel.Children.Add(picker);
        panel.Children.Add(toggle);

        return new SettingsCard
        {
            Header = profile.Name,
            Description = profile.Command,
            Control = panel,
        };
    }

    // The SettingsCard.Control slot is a horizontal StackPanel
    // (picker + toggle), so finding either child means walking the
    // panel's Children rather than pattern-matching the slot directly.
    private static ToggleSwitch? FindToggle(SettingsCard card)
    {
        if (card.Control is not StackPanel panel) return null;
        foreach (var child in panel.Children)
        {
            if (child is ToggleSwitch t) return t;
        }
        return null;
    }

    private static ProfileIconPickerControl? FindPicker(SettingsCard card)
    {
        if (card.Control is not StackPanel panel) return null;
        foreach (var child in panel.Children)
        {
            if (child is ProfileIconPickerControl p) return p;
        }
        return null;
    }

    private void OnHiddenToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.Tag is not string id) return;

        // Mirror the AppearancePage / RawEditorPage pattern: suppress the
        // FileSystemWatcher around our own write so its 300ms debounce
        // doesn't double-fire Reload, then call Reload explicitly so the
        // in-memory ProfileView updates and the page rebinds
        // deterministically rather than depending on when the watcher's
        // debounce timer happens to land.
        //
        // Hide via SetValue("true"); un-hide via RemoveValue rather than
        // SetValue("false") so the config stays minimal -- hidden
        // defaults to false, so a stray "false" line is just noise that
        // also confuses the warnings filter for hidden-only blocks.
        var key = ProfileHiddenKey.For(id);
        _configService.SuppressWatcher(true);
        try
        {
            if (toggle.IsOn) _editor.SetValue(key, "true");
            else _editor.RemoveValue(key);
        }
        finally { _configService.SuppressWatcher(false); }
        _configService.Reload();
    }

    private void OnProfileIconChanged(object? sender, IconSpec? newSpec)
    {
        if (_loading) return;
        if (sender is not ProfileIconPickerControl picker) return;
        // Recover the profile id from the row's StackPanel Tag (set in
        // BuildRow), the same path FindPicker walks in Rebind.
        if (picker.Parent is not StackPanel panel) return;
        if (panel.Tag is not string id) return;

        var key = $"profile.{id}.icon";
        var value = newSpec switch
        {
            // Persisted format mirrors ProfileSourceParser.ParseIcon so
            // a written value round-trips back to the same IconSpec.
            // Dpi is intentionally dropped: the resolver picks DPI from
            // the monitor at render time; storing 32 here would freeze
            // a stale value into the config.
            IconSpec.BrandKey b => $"brand:{b.Key}",
            IconSpec.Mdl2Token m => "mdl2:" + m.CodePoint.ToString("X4", CultureInfo.InvariantCulture),
            IconSpec.Path p => p.FilePath,
            _ => null,
        };

        _configService.SuppressWatcher(true);
        try
        {
            if (value is null) _editor.RemoveValue(key);
            else _editor.SetValue(key, value);
        }
        finally { _configService.SuppressWatcher(false); }
        _configService.Reload();
    }
}
