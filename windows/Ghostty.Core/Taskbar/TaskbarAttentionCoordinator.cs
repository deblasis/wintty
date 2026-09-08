using System;
using Ghostty.Core.Notifications;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Taskbar;

/// <summary>
/// The Windows `bell-features = attention` producer: an unfocused bell
/// raises the bell badge through <see cref="TaskbarBadgeArbiter"/>, regaining
/// focus clears it. Each episode carries a fresh signature so the notice
/// comes back for the next background bell after the user dismissed one.
/// </summary>
internal sealed class TaskbarAttentionCoordinator
{
    public const string Key = "bell";

    private readonly TaskbarBadgeArbiter _badges;
    private bool _focused;
    private bool _attentionActive;
    private long _episode;

    public TaskbarAttentionCoordinator(TabManager manager, TaskbarBadgeArbiter badges)
    {
        _badges = badges;
        manager.BellRang += (_, features) => { if (features.Attention) OnBell(); };
    }

    public void OnBell()
    {
        if (_focused) return;
        if (_attentionActive) return;
        _attentionActive = true;
        _episode++;
        _badges.Raise(new TaskbarBadge(
            Key,
            TaskbarBadgeKind.Bell,
            _episode.ToString(),
            "Bell",
            "A bell rang in a tab while this window was in the background. The taskbar badge clears when the window is focused.",
            NoticeSeverity.Informational,
            Array.Empty<NoticeAction>()));
    }

    public void SetFocused(bool focused)
    {
        _focused = focused;
        if (focused && _attentionActive)
        {
            _attentionActive = false;
            _badges.Clear(Key);
        }
    }
}
