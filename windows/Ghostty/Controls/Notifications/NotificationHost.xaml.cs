using System.Collections.Generic;
using System.Collections.Specialized;
using Ghostty.Core.Notifications;
using Microsoft.UI.Xaml;
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

    public NotificationHost()
    {
        InitializeComponent();
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
                break;
        }
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

        _bars[notice] = bar;
        Stack.Children.Add(bar);
    }

    private void RemoveBar(Notice notice)
    {
        if (_bars.Remove(notice, out var bar))
        {
            bar.IsOpen = false;
            Stack.Children.Remove(bar);
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

    private static InfoBarSeverity ToInfoBarSeverity(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Success => InfoBarSeverity.Success,
        NoticeSeverity.Warning => InfoBarSeverity.Warning,
        NoticeSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };
}
