using System;
using Ghostty.Core.Search;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Ghostty.Controls.Search;

/// <summary>
/// In-pane scrollback search overlay. Sits above the terminal surface
/// when the user invokes search and forwards needle / navigation /
/// close intents to an <see cref="ISearchHost"/> that wraps the
/// underlying libghostty surface.
///
/// Consumers Visibility-toggle the control and call
/// <see cref="FocusNeedle"/> after showing it (WinUI does not auto-focus
/// a control that merely becomes visible). The control raises
/// <see cref="Closed"/> when the user clicks the close button or
/// presses Escape so the parent can hide it; the parent is responsible
/// for deciding whether to also call <see cref="SearchState.Reset"/>.
///
/// Typing in the needle box is debounced (80ms) before calling
/// <see cref="ISearchHost.StartSearch"/> so each keystroke does not
/// kick off a fresh libghostty search.
/// </summary>
public sealed partial class SearchBarControl : UserControl
{
    // 80ms is long enough to coalesce keystrokes from a fast typist
    // but short enough to feel responsive on a slow drag-replace.
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(80);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _debounceTimer;

    public SearchBarControl()
    {
        State = new SearchState();
        InitializeComponent();

        // DispatcherQueueTimer fires on the UI thread, so no marshalling
        // is needed when the tick handler reads NeedleBox.Text. The
        // timer is created once and reused; Start() resets it on each
        // keystroke so it only fires after the user pauses typing.
        _debounceTimer = DispatcherQueue.CreateTimer();
        _debounceTimer.Interval = DebounceInterval;
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += OnDebounceTick;

        // Drop the Tick handler when the control leaves the tree so a
        // reparented or torn-down pane doesn't accumulate handler links
        // on the dispatcher's timer list.
        Unloaded += OnControlUnloaded;
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTick;
        Unloaded -= OnControlUnloaded;
    }

    /// <summary>
    /// Observable search state bound by the XAML. The control owns the
    /// instance; the per-pane controller mutates Total/Selected as
    /// libghostty match callbacks arrive.
    /// </summary>
    public SearchState State { get; }

    public static readonly DependencyProperty SearchHostProperty =
        DependencyProperty.Register(
            nameof(SearchHost),
            typeof(ISearchHost),
            typeof(SearchBarControl),
            new PropertyMetadata(null));

    /// <summary>
    /// The host that translates UI intents into libghostty binding
    /// actions. Typically set by the parent (TerminalControl /
    /// PaneHost) once the surface is ready.
    /// </summary>
    public ISearchHost? SearchHost
    {
        get => (ISearchHost?)GetValue(SearchHostProperty);
        set => SetValue(SearchHostProperty, value);
    }

    /// <summary>
    /// Raised when the user closes the bar (close-button click or Esc
    /// in the needle box). The control has already called
    /// <see cref="ISearchHost.EndSearch"/> on the host before raising;
    /// listeners typically toggle Visibility and may choose to reset
    /// the <see cref="State"/>.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Move keyboard focus into the needle box and select all existing
    /// text. Selecting on focus lets the user immediately type to
    /// replace the previous query rather than appending to it, which
    /// matches the convention in most find UIs.
    /// </summary>
    public void FocusNeedle()
    {
        NeedleBox.Focus(FocusState.Programmatic);
        NeedleBox.SelectAll();
    }

    /// <summary>
    /// True while keyboard focus is anywhere inside the search bar (the
    /// needle box or the prev/next/close buttons). The parent reads this
    /// to gate terminal key forwarding: the bar is a child of
    /// TerminalControl's visual tree, so its KeyDown and CharacterReceived
    /// events bubble up to the TerminalControl handlers that forward input
    /// to libghostty. Without this gate, keystrokes (and characters, while
    /// the needle is focused) would also reach the shell.
    ///
    /// Tracking live focus rather than the bar's open state is deliberate:
    /// the bar can stay visible after the user clicks back into the
    /// terminal surface, and in that case typing must keep flowing to the
    /// shell. Containment (not just the needle) matters because focusing a
    /// nav button would otherwise re-open the same leak for keys the button
    /// does not consume.
    /// </summary>
    public bool ContainsFocus
    {
        get
        {
            // XamlRoot is null until the control is loaded; no focus to own.
            if (XamlRoot is null) return false;
            var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (node is not null)
            {
                if (ReferenceEquals(node, this)) return true;
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }

    // ── Event handlers ────────────────────────────────────────────────

    private void OnNeedleTextChanged(object _, TextChangedEventArgs __)
    {
        // Each keystroke resets the timer; the tick only fires once
        // the user pauses for DebounceInterval. Start() on an already-
        // running timer cancels the pending tick and reschedules from
        // now, which is the behavior we want.
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTick(Microsoft.UI.Dispatching.DispatcherQueueTimer _, object __)
    {
        // The two-way x:Bind on Text has already pushed the latest
        // value into State.Needle; read from the TextBox directly so
        // we forward the exact string the user sees, even if the bind
        // ever lags behind a paste burst.
        SearchHost?.StartSearch(NeedleBox.Text);
    }

    private void OnNeedleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                {
                    var shift = (Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(VirtualKey.Shift)
                        & Windows.UI.Core.CoreVirtualKeyStates.Down)
                        == Windows.UI.Core.CoreVirtualKeyStates.Down;
                    if (shift)
                        SearchHost?.NavigatePrevious();
                    else
                        SearchHost?.NavigateNext();
                    e.Handled = true;
                    break;
                }

            case VirtualKey.Escape:
                RaiseClosed();
                e.Handled = true;
                break;
        }
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) =>
        SearchHost?.NavigatePrevious();

    private void OnNextClick(object sender, RoutedEventArgs e) =>
        SearchHost?.NavigateNext();

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        RaiseClosed();

    private void RaiseClosed()
    {
        // Stop any pending debounce tick so we do not fire a stale
        // StartSearch after the host has already torn the search down.
        _debounceTimer.Stop();
        SearchHost?.EndSearch();
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
