using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Ghostty.Settings;

/// <summary>
/// Captures a single keyboard chord and previews it, warning when the chord is
/// already bound. The chosen ghostty trigger token is exposed via
/// <see cref="CapturedTrigger"/>; the caller writes it on Primary.
/// </summary>
internal sealed partial class RebindDialog : ContentDialog
{
    private readonly IReadOnlyList<EnumeratedKeybind> _current;
    private readonly string _action;

    /// <summary>The encoded ghostty trigger token chosen, or null if none/invalid.</summary>
    public string? CapturedTrigger { get; private set; }

    public RebindDialog(
        IReadOnlyList<EnumeratedKeybind> current,
        string action,
        string actionFriendly)
    {
        _current = current;
        _action = action;
        InitializeComponent();
        ActionText.Text = $"Shortcut for: {actionFriendly}";

        // Capture on the dialog's tunneling PreviewKeyDown rather than the
        // Border's KeyDown. The ContentDialog activates its default (Assign)
        // button from a key handler on itself; because PreviewKeyDown tunnels
        // root->leaf, the dialog sees the key before the focused Border would,
        // so marking it Handled here is what actually stops Enter/Space/Tab
        // from triggering the primary button. That lets the user capture
        // ctrl+enter, ctrl+space, etc. as real chords without closing.
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => CaptureBox.Focus(FocusState.Programmatic);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Only intercept while the capture box owns focus; otherwise let the
        // dialog behave normally (e.g. Esc/Enter on the buttons themselves).
        if (CaptureBox.FocusState == FocusState.Unfocused) return;

        e.Handled = true; // don't let Tab/Enter/Space leak to dialog buttons

        var trigger = ChordEncoder.TryEncode(
            (int)e.Key,
            IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift),
            IsDown(VirtualKey.Menu),
            IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows));

        if (trigger is null)
        {
            CapturedTrigger = null;
            IsPrimaryButtonEnabled = false;
            PreviewText.Text = "Unsupported key - press another";
            ConflictBar.IsOpen = false;
            return;
        }

        var kb = new EnumeratedKeybind(new[] { trigger.Value }, _action, GhosttyBindingFlags.Consumed);
        CapturedTrigger = KeybindTriggerSyntax.Encode(kb);
        PreviewText.Text = TriggerLabeler.Describe(kb);
        IsPrimaryButtonEnabled = true;

        var existing = KeybindConflicts.FindByTrigger(_current, CapturedTrigger);
        if (existing is not null && existing.Action != _action)
        {
            ConflictBar.Message =
                $"Already bound to {KeybindActionCatalog.Describe(existing.Action).Friendly}. Assign will override it.";
            ConflictBar.IsOpen = true;
        }
        else
        {
            ConflictBar.IsOpen = false;
        }
    }

    // Reads live modifier state the same way as
    // TerminalControl.CurrentChordModifiers (Microsoft.UI.Input source +
    // Windows.UI.Core key states); extended here to cover the Windows key.
    private static bool IsDown(VirtualKey key)
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
}
