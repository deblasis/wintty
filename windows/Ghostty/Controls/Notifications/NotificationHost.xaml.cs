using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Ghostty.Core.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Controls.Notifications;

/// <summary>
/// Renders the shared <see cref="INotificationService.Active"/> collection as a
/// bottom-anchored stack of InfoBars — one per <see cref="Notice"/>. This is the
/// only place notice models become WinUI controls; features raise notices
/// through the service and never touch XAML.
///
/// <para>
/// Binds imperatively rather than via an ItemsControl so each notice's variable
/// action set (0..N buttons, primary styling, per-action dismiss) maps cleanly
/// without value converters, and so InfoBar's own close affordance routes back
/// through <see cref="INotificationService.Dismiss"/>.
/// </para>
/// </summary>
public sealed partial class NotificationHost : UserControl
{
    private INotificationService? _service;
    private bool _subscribed;
    private readonly Dictionary<Notice, InfoBar> _bars = new();
    private readonly Dictionary<Notice, Microsoft.UI.Dispatching.DispatcherQueueTimer> _timers = new();

    /// <summary>Set by the window: returns focus to the active terminal after
    /// a focused bar leaves. Null in tests and before Attach.</summary>
    public Action? FocusReturn { get; set; }

    public NotificationHost()
    {
        InitializeComponent();
        // WinUI folds Margin into DesiredSize even when Stack has zero
        // children, so an empty host would still report the StackPanel's own
        // ~16px and keep the Auto dock row -- and this control's opaque
        // background -- permanently open. A Collapsed element reports (0,0)
        // DesiredSize regardless of its content's Margin, which is what "no
        // notices, no row" actually needs. See UpdateVisibility.
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Bind this host to the shared service. Idempotent. Records the service and
    /// wires the Loaded/Unloaded lifecycle; the collection subscription follows
    /// the element's live state, so a spurious Unloaded→Loaded cycle (WinUI can
    /// fire these on reparenting / monitor moves) re-binds instead of leaving the
    /// host silently detached. Renders any notices already active (e.g. one
    /// raised at startup before the window existed).
    /// </summary>
    public void Attach(INotificationService service)
    {
        _service = service;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        if (IsLoaded) Subscribe();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Subscribe();

    private void Subscribe()
    {
        if (_service is null || _subscribed) return;
        ((INotifyCollectionChanged)_service.Active).CollectionChanged += OnActiveChanged;
        _subscribed = true;
        foreach (var notice in _service.Active) AddBar(notice);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Keep _service so a later Loaded re-subscribes; only drop the collection
        // subscription and the materialized bars.
        if (_service is not null && _subscribed)
            ((INotifyCollectionChanged)_service.Active).CollectionChanged -= OnActiveChanged;
        _subscribed = false;
        Stack.Children.Clear();
        _bars.Clear();
        StopAllTimers();
        UpdateVisibility();
    }

    private void OnActiveChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (Notice n in e.NewItems) AddBar(n);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (Notice n in e.OldItems) RemoveBar(n);
                break;
            case NotifyCollectionChangedAction.Reset:
                Stack.Children.Clear();
                _bars.Clear();
                StopAllTimers();
                UpdateVisibility();
                break;
        }
    }

    /// <summary>
    /// The dock row is genuinely zero height, and this control paints
    /// nothing, exactly when there is nothing to show. Called from every
    /// path that changes <see cref="_bars"/>, so all of them agree on what
    /// "collapse" means rather than each reaching for Visibility by hand.
    /// </summary>
    private void UpdateVisibility() =>
        Visibility = _bars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Stop every armed auto-dismiss timer without dismissing the notices
    /// they belong to. Used by the two paths that clear <see cref="_bars"/>
    /// out from under the timers instead of going through
    /// <see cref="RemoveBar"/> one notice at a time.
    /// </summary>
    private void StopAllTimers()
    {
        foreach (var timer in _timers.Values) timer.Stop();
        _timers.Clear();
    }

    private void AddBar(Notice notice)
    {
        if (_bars.ContainsKey(notice)) return;

        var bar = new InfoBar
        {
            Title = notice.Title,
            Message = notice.Message,
            Severity = ToInfoBarSeverity(notice.Severity),
            IsClosable = notice.IsClosable,
            IsOpen = true,
        };
        // Title is visual-only on InfoBar; UIA Name stays empty unless set,
        // so a harness/narrator looking for the notice title would miss it.
        AutomationProperties.SetName(bar, notice.Title);
        // An id as well as a name, because Name here is the visible copy and
        // the copy is the part that gets reworded. A harness that can only
        // match on the wording reports "no banner" after an edit to it, which
        // reads as the feature working. DedupKey is the notice's own stable
        // identity wherever it has one; a notice without one falls back to a
        // slug of its title rather than a shared constant, because UIA expects
        // an id to be unique among siblings and two banners answering to the
        // same one make FindFirst return an arbitrary member of the pair.
        AutomationProperties.SetAutomationId(
            bar,
            "Notice_" + (string.IsNullOrEmpty(notice.DedupKey) ? Slug(notice.Title) : notice.DedupKey));

        if (notice.Actions.Count == 1)
        {
            bar.ActionButton = MakeButton(notice, notice.Actions[0]);
        }
        else if (notice.Actions.Count > 1)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            foreach (var action in notice.Actions)
                panel.Children.Add(MakeButton(notice, action));
            bar.Content = panel;
        }

        // The built-in X routes back through the service so OnDismiss fires and
        // the collection stays the single source of truth (the Remove then
        // drives RemoveBar).
        bar.CloseButtonClick += (_, _) => _service?.Dismiss(notice);

        // A transient notice dismisses itself after its own timeout: the
        // caller does not have to hold a reference or race the dispatcher.
        if (notice.AutoDismissAfter is { } after)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = after;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => _service?.Dismiss(notice);
            _timers[notice] = timer;
            timer.Start();
        }

        // A notice raised from a command the user just ran already has the
        // user's attention, so the bar takes focus and Enter or Space acts
        // as its dismiss; a notice that arrives on its own leaves focus
        // wherever it was.
        if (notice.FocusOnShow)
        {
            bar.IsTabStop = true;
            bar.KeyDown += (_, e) =>
            {
                if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                {
                    e.Handled = true;
                    _service?.Dismiss(notice);
                }
            };
            bar.Loaded += (s, _) => ((Control)s).Focus(FocusState.Programmatic);
        }

        _bars[notice] = bar;
        Stack.Children.Add(bar);
        UpdateVisibility();
    }

    private void RemoveBar(Notice notice)
    {
        if (_bars.Remove(notice, out var bar))
        {
            bar.IsOpen = false;
            if (_timers.Remove(notice, out var timer)) timer.Stop();
            var hadFocus = notice.FocusOnShow;
            Stack.Children.Remove(bar);
            UpdateVisibility();
            if (hadFocus) FocusReturn?.Invoke();
        }
    }

    private Button MakeButton(Notice notice, NoticeAction action)
    {
        var button = new Button { Content = action.Label };
        if (action.IsPrimary && Application.Current.Resources.TryGetValue(
                "AccentButtonStyle", out var style) && style is Style s)
        {
            button.Style = s;
        }
        button.Click += (_, _) =>
        {
            action.Invoke();
            if (action.DismissesNotice) _service?.Dismiss(notice);
        };
        return button;
    }

    /// <summary>
    /// Lowercase, hyphen-separated, letters and digits only, so the fallback
    /// AutomationId reads like the dedup keys the other notices use.
    /// </summary>
    private static string Slug(string title)
    {
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        return sb.ToString().TrimEnd('-');
    }

    private static InfoBarSeverity ToInfoBarSeverity(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Success => InfoBarSeverity.Success,
        NoticeSeverity.Warning => InfoBarSeverity.Warning,
        NoticeSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };
}
