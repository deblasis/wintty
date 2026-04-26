using System;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Input;

/// <summary>
/// Pure-logic classifier for new-tab split-button click events.
/// Matches Windows Terminal's documented modifier convention: Alt
/// opens in a new pane in the active tab, Shift opens in a new
/// window. Shift takes precedence over Alt because new-window
/// subsumes new-pane in WT.
/// </summary>
public static class ClickModifierClassifier
{
    public static ProfileLaunchTarget Classify(IModifierKeyState modifiers)
    {
        ArgumentNullException.ThrowIfNull(modifiers);
        if (modifiers.IsShiftDown) return ProfileLaunchTarget.NewWindow;
        if (modifiers.IsAltDown) return ProfileLaunchTarget.NewPane;
        return ProfileLaunchTarget.NewTab;
    }
}
