using System;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Input;

/// <summary>
/// Pure transforms on the user config's repeatable `keybind` line list (the
/// values, e.g. "ctrl+key_t=new_tab"). Defaults live compiled in libghostty,
/// not the file, so these lines are only user customizations.
/// </summary>
public static class UserKeybindEditor
{
    /// <summary>Append "&lt;trigger&gt;=unbind" if not already present (idempotent).</summary>
    public static string[] Unbind(IReadOnlyList<string> lines, EnumeratedKeybind binding)
    {
        var trigger = KeybindTriggerSyntax.Encode(binding);
        var unbindLine = trigger + "=unbind";
        var canonical = KeybindTriggerSyntax.Canonicalize(trigger);
        var already = lines.Any(l =>
            string.Equals(TriggerOf(l), canonical, StringComparison.Ordinal) &&
            string.Equals(ActionOf(l), "unbind", StringComparison.Ordinal));
        return already ? lines.ToArray() : lines.Append(unbindLine).ToArray();
    }

    /// <summary>
    /// Remove every user line whose (whole) trigger matches the binding's trigger,
    /// reverting that trigger fully to its compiled default. This is intentionally
    /// trigger-only (not trigger+action like Unbind): "reset this trigger" drops all
    /// of the user's customizations of it, which is correct under last-wins where only
    /// one user line for a trigger is ever active. A sequence line that merely shares
    /// the first step has a different whole-trigger canonical form and is NOT removed.
    /// </summary>
    public static string[] Reset(IReadOnlyList<string> lines, EnumeratedKeybind binding)
    {
        var canonical = KeybindTriggerSyntax.Canonicalize(KeybindTriggerSyntax.Encode(binding));
        return lines.Where(l => !string.Equals(TriggerOf(l), canonical, StringComparison.Ordinal)).ToArray();
    }

    private static string TriggerOf(string line)
    {
        var eq = line.IndexOf('=');
        var trigger = eq < 0 ? line : line[..eq];
        return KeybindTriggerSyntax.Canonicalize(trigger.Trim());
    }

    private static string ActionOf(string line)
    {
        var eq = line.IndexOf('=');
        return eq < 0 ? string.Empty : line[(eq + 1)..].Trim();
    }
}
