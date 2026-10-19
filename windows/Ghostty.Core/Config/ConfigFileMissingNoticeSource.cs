using System;
using Ghostty.Core.Notifications;

namespace Ghostty.Core.Config;

/// <summary>
/// The host side of a concluded config-file deletion: one notice, up for
/// exactly as long as the file is gone, offering to load the defaults.
/// </summary>
/// <remarks>
/// <para>The machine (<see cref="ConfigFilePresence"/>) decides WHEN; this
/// decides WHAT the user sees and keeps the banner's own bookkeeping. The
/// split is the one <see cref="Ghostty.Core.Renderer.RendererHealthNoticeSource"/>
/// uses: the banner state is behaviour a test drives, against a real
/// <see cref="INotificationService"/>, without a window.</para>
///
/// <para>Three producers reach the same banner, and they are idempotent
/// against each other: the machine concluding (the watcher path), the
/// vanish protocol accepting (the no-watcher path, where deletion can only
/// be proven through load verdicts), and the backstop poll firing late
/// behind an earlier conclusion. The dedup key plus the source's own
/// active-notice record mean none of them can stack a second banner, and
/// the user closing the banner does not turn into a flap: a new conclusion
/// re-raises, because it is news again.</para>
///
/// <para>The banner comes down when its premise stops being true, and the
/// premise is "the file is gone". The machine resolving (a reappearance)
/// and an applied reload both take it down. Last event wins: a save that
/// lands while the banner is up refreshes the session's config and dismisses
/// the notice; the load-defaults action racing a reappearance is answered by
/// whatever the disk holds at the moment the reload builds, because every
/// reload builds from the disk as it is now.</para>
///
/// <para>UI-free and dependency-free but for the notification abstraction,
/// so the copy, the action wiring and the show/dismiss bookkeeping
/// unit-test without the WinUI runtime. Not thread safe: everything here
/// runs on the thread the host loads the config on, like the machine's own
/// callers.</para>
/// </remarks>
public sealed class ConfigFileMissingNoticeSource
{
    /// <summary>One key for the one config file, so a second producer's
    /// show while the banner is up is a no-op rather than a stack.</summary>
    public const string DedupKey = "config-file-missing";

    private readonly ConfigFilePresence _presence;
    private readonly Func<INotificationService?> _notifications;
    private readonly Func<TimeSpan, bool> _scheduleBackstopPoll;
    private readonly Action? _cancelBackstopPoll;
    private readonly Action _loadDefaults;
    private readonly Action? _onShown;
    private readonly Action? _onDismissed;

    // The banner currently on screen, kept because Dismiss takes the
    // instance. Null exactly when nothing is shown.
    private Notice? _active;

    /// <param name="presence">The machine. Subscribed here: its state moves
    /// are this source's inputs.</param>
    /// <param name="notifications">Answers with the service to show on, or
    /// null while none exists yet. Read at show time, so a source built
    /// before the app's notice queue (as the config service is) still finds
    /// it.</param>
    /// <param name="scheduleBackstopPoll">Arms one poll of the machine,
    /// <paramref name="presence"/>'s <c>UntilBackstop</c> out, on the
    /// thread the host loads the config on. The poll itself calls back
    /// through <see cref="OnBackstopPollDue"/>. A pending poll replaced by
    /// a later schedule is fine: an early poll concludes nothing.</param>
    /// <param name="loadDefaults">What the action does: the host sets its
    /// one-shot request and reloads. Runs on the UI thread, from the
    /// button.</param>
    /// <param name="onShown">Logged by the host; invoked once per show.</param>
    /// <param name="onDismissed">Logged by the host; invoked once per
    /// dismissal, from any of the paths that take the banner down.</param>
    public ConfigFileMissingNoticeSource(
        ConfigFilePresence presence,
        Func<INotificationService?> notifications,
        Func<TimeSpan, bool> scheduleBackstopPoll,
        Action loadDefaults,
        Action? cancelBackstopPoll = null,
        Action? onShown = null,
        Action? onDismissed = null)
    {
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(scheduleBackstopPoll);
        ArgumentNullException.ThrowIfNull(loadDefaults);

        _presence = presence;
        _notifications = notifications;
        _scheduleBackstopPoll = scheduleBackstopPoll;
        _cancelBackstopPoll = cancelBackstopPoll;
        _loadDefaults = loadDefaults;
        _onShown = onShown;
        _onDismissed = onDismissed;
        _presence.StateChanged += OnPresenceChanged;
    }

    /// <summary>Whether the banner is up right now. For tests and for the
    /// host's own guards.</summary>
    public bool NoticeShowing => _active is not null;

    /// <summary>The machine moved. PendingDelete arms the one poll that can
    /// conclude it; Gone shows; Present takes the banner down and retires
    /// the armed poll, which would otherwise fire into a state that cannot
    /// conclude.</summary>
    public void OnPresenceChanged(ConfigFilePresenceChange change)
    {
        if (!change.StateChanged) return;
        switch (change.State)
        {
            case ConfigFilePresenceState.PendingDelete:
                _scheduleBackstopPoll(_presence.UntilBackstop);
                break;
            case ConfigFilePresenceState.Gone:
                Show();
                break;
            case ConfigFilePresenceState.Present:
                _cancelBackstopPoll?.Invoke();
                Dismiss();
                break;
        }
    }

    /// <summary>The poll the source itself scheduled: ask the machine
    /// whether the pending delete has outlived its backstop. An early or a
    /// repeated fire is answered "no" by the machine and costs nothing.</summary>
    public void OnBackstopPollDue()
    {
        var change = _presence.Poll();
        if (change.StateChanged && change.State == ConfigFilePresenceState.Gone)
            Show();
    }

    /// <summary>The vanish protocol accepted a deletion on the no-watcher
    /// path, where no event will ever tell the machine. Same banner, same
    /// premise, idempotent against the machine's own conclusion.</summary>
    public void OnDeletionConfirmed() => Show();

    /// <summary>A reload applied: the config in force was just rebuilt from
    /// the disk as it stands, so the banner's premise is over whatever the
    /// rebuild found. Idempotent.</summary>
    public void OnReloadApplied() => Dismiss();

    private void Show()
    {
        if (_active is not null) return;
        var notifications = _notifications();
        if (notifications is null) return;

        Notice? raised = null;
        raised = Build(() =>
        {
            // OnDismiss fires for our own Dismiss as well as for the close
            // X, and only a banner that is still ours should clear the
            // record: comparing against _active tells them apart, the same
            // way RendererHealthNoticeSource does.
            if (!ReferenceEquals(_active, raised)) return;
            _active = null;
        });
        _active = raised;
        notifications.Show(raised);
        _onShown?.Invoke();
    }

    private void Dismiss()
    {
        var notice = _active;
        if (notice is null) return;
        _active = null;
        _notifications()?.Dismiss(notice);
        _onDismissed?.Invoke();
    }

    private Notice Build(Action onDismiss) => new()
    {
        Title = "Config file missing",
        // The copy is the owner's, verbatim: reassurance first, because the
        // settings the session is running are intact. The headline above it
        // is the only wording of the app's own.
        Message = "config file missing, still using the settings you had",
        // Informational, not Warning: nothing the user has is at risk. The
        // settings are running and the file is the only thing missing.
        Severity = NoticeSeverity.Informational,
        DedupKey = DedupKey,
        IsClosable = true,
        Actions = new[]
        {
            // The user's way out, and the only one that applies a config
            // built from no file: the ask is the host's one-shot key, and
            // the reload it asks for builds from the disk as it stands at
            // that moment, so a file that returned in between wins.
            new NoticeAction("load defaults", _loadDefaults, IsPrimary: true),
        },
        // A notice that arrives on its own does not take the focus.
        FocusOnShow = false,
        OnDismiss = onDismiss,
    };
}
