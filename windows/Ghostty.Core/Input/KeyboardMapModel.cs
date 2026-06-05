using System.Collections.Generic;

namespace Ghostty.Core.Input;

/// <summary>State of one bound key on the current modifier layer.</summary>
public sealed record KeyboardKeyState(
    string KeyName,
    bool IsBound,
    string ActionLabel,
    string RawAction,
    KeybindSource Source,
    KeybindConflict Conflict,
    bool IsMultiStep,
    EnumeratedKeybind Bind);

/// <summary>
/// Projects the finalized keybind set onto a single modifier layer: which
/// physical keys are bound when exactly the given modifier combo is held, with
/// the action label, source, conflict, and multi-step flag for each. Pure; the
/// WinUI view renders the result and the page edits via the source Bind.
/// </summary>
public sealed class KeyboardMapModel
{
    private const uint Shift = 1u << 0, Ctrl = 1u << 1, Alt = 1u << 2, Super = 1u << 3;
    private const uint ShiftR = 1u << 6, CtrlR = 1u << 7, AltR = 1u << 8, SuperR = 1u << 9;
    private const int TagPhysical = 0;

    public uint ModifierMask { get; }
    private readonly Dictionary<string, KeyboardKeyState> _byKey;

    private KeyboardMapModel(uint mask, Dictionary<string, KeyboardKeyState> byKey)
    {
        ModifierMask = mask;
        _byKey = byKey;
    }

    /// <summary>State for a physical key on this layer, or null if unbound.</summary>
    public KeyboardKeyState? ForKey(string keyName)
        => _byKey.TryGetValue(keyName, out var s) ? s : null;

    public static KeyboardMapModel Build(
        IReadOnlyList<EnumeratedKeybind> binds,
        IReadOnlyList<EnumeratedKeybind>? defaults,
        uint modifierMask)
    {
        var defaultKeys = defaults is null ? null : KeybindSourceClassifier.BuildDefaultKeys(defaults);
        var byKey = new Dictionary<string, KeyboardKeyState>();

        foreach (var kb in binds)
        {
            if (kb.Steps.Count == 0) continue;
            var first = kb.Steps[0];
            if (first.Tag != TagPhysical) continue;
            if (Canonical(first.Mods) != modifierMask) continue;
            var name = KeyNames.NameOf((int)first.Key);
            if (name is null) continue;

            var source = defaultKeys is null
                ? KeybindSource.Default
                : KeybindSourceClassifier.Classify(kb, defaultKeys);

            byKey[name] = new KeyboardKeyState(
                name,
                IsBound: true,
                ActionLabel: KeybindActionCatalog.Describe(kb.Action).Friendly,
                RawAction: kb.Action,
                Source: source,
                Conflict: KeybindConflictAnalyzer.Analyze(kb),
                IsMultiStep: kb.Steps.Count > 1,
                Bind: kb);
        }

        return new KeyboardMapModel(modifierMask, byKey);
    }

    private static uint Canonical(uint mods)
    {
        // caps_lock(bit 4) / num_lock(bit 5) intentionally dropped — not modifier-layer bits.
        uint m = 0;
        if ((mods & (Shift | ShiftR)) != 0) m |= Shift;
        if ((mods & (Ctrl | CtrlR)) != 0) m |= Ctrl;
        if ((mods & (Alt | AltR)) != 0) m |= Alt;
        if ((mods & (Super | SuperR)) != 0) m |= Super;
        return m;
    }
}
