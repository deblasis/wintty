using System.Collections.Generic;

namespace Ghostty.Core.Input;

public enum KeybindSource
{
    Default,
    User,
}

/// <summary>
/// Classifies each current binding as Default or User by diffing against a
/// default-only enumeration. A binding is User when its (canonical trigger,
/// action) pair is absent from the defaults (covers rebinds and user-added).
/// </summary>
public static class KeybindSourceClassifier
{
    // '|' cannot appear in an encoded trigger (mods/'+'/'>'/key-name) or in an
    // action string, so distinct (trigger, action) pairs never collide.
    private const char Separator = '|';

    public static KeybindSource Classify(EnumeratedKeybind kb, HashSet<string> defaultKeys)
        => defaultKeys.Contains(KeyOf(kb)) ? KeybindSource.Default : KeybindSource.User;

    public static HashSet<string> BuildDefaultKeys(IReadOnlyList<EnumeratedKeybind> defaults)
    {
        var set = new HashSet<string>();
        foreach (var d in defaults) set.Add(KeyOf(d));
        return set;
    }

    private static string KeyOf(EnumeratedKeybind kb)
        => KeybindTriggerSyntax.Encode(kb) + Separator + kb.Action;
}
