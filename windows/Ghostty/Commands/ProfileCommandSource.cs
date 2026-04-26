using System;
using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Profiles;
using Ghostty.Input;

namespace Ghostty.Commands;

/// <summary>
/// Command palette source that emits one row per visible profile in
/// IProfileRegistry.Profiles order. The Execute lambda reads
/// IModifierKeyState at click/Enter time and routes through the
/// injected openProfile delegate via ClickModifierClassifier, so
/// Alt/Shift behave the same as the new-tab split button (PR 4).
///
/// Refreshed every time the palette opens (CommandPaletteViewModel.Open
/// calls Refresh on each source). No event subscription, so no dispose
/// lifecycle to coordinate with the registry.
///
/// No unit tests: this type lives in the Ghostty WinUI assembly which
/// Ghostty.Tests cannot reference. Slot resolution (the only non-trivial
/// logic) is covered by ProfileSlotResolverTests; the rest is verified
/// via the manual matrix.
/// </summary>
internal sealed class ProfileCommandSource : ICommandSource
{
    private readonly IProfileRegistry _registry;
    private readonly IModifierKeyState _modifiers;
    private readonly Action<string, ProfileLaunchTarget> _openProfile;
    private List<CommandItem>? _cache;

    public ProfileCommandSource(
        IProfileRegistry registry,
        IModifierKeyState modifiers,
        Action<string, ProfileLaunchTarget> openProfile)
    {
        _registry = registry;
        _modifiers = modifiers;
        _openProfile = openProfile;
    }

    public IReadOnlyList<CommandItem> GetCommands()
    {
        _cache ??= BuildCommands();
        return _cache;
    }

    public void Refresh() => _cache = null;

    private List<CommandItem> BuildCommands()
    {
        var profiles = _registry.Profiles;
        var commands = new List<CommandItem>(profiles.Count);

        for (int i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var capturedId = profile.Id;
            var title = profile.IsDefault ? profile.Name + "  *" : profile.Name;

            commands.Add(new CommandItem
            {
                Id = $"profile:{profile.Id}",
                Title = title,
                Description = $"Open {profile.Name} as a new tab (Alt: new pane, Shift: new window)",
                ActionKey = $"open_profile:{profile.Id}",
                Category = CommandCategory.Tab,
                // TODO: PR 6 - per-profile icon (use IIconResolver + Profile.Icon).
                LeadingIcon = "",
                Shortcut = LookupShortcut(SlotAction(i)),
                Execute = _ =>
                {
                    var target = ClickModifierClassifier.Classify(_modifiers);
                    _openProfile(capturedId, target);
                },
            });
        }

        return commands;
    }

    private static PaneAction? SlotAction(int index) => index switch
    {
        0 => PaneAction.OpenProfile1,
        1 => PaneAction.OpenProfile2,
        2 => PaneAction.OpenProfile3,
        3 => PaneAction.OpenProfile4,
        4 => PaneAction.OpenProfile5,
        5 => PaneAction.OpenProfile6,
        6 => PaneAction.OpenProfile7,
        7 => PaneAction.OpenProfile8,
        8 => PaneAction.OpenProfile9,
        _ => null,
    };

    private static KeyBinding? LookupShortcut(PaneAction? action) =>
        action is null ? null : KeyBindings.Default.Find(action.Value);
}
