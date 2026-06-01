using System.Collections.Generic;
using Ghostty.Core.Interop;

namespace Ghostty.Core.Input;

/// <summary>One physical trigger step (modifiers + key) of a keybind.</summary>
public readonly record struct KeybindTrigger(int Tag, uint Key, uint Mods);

/// <summary>
/// A single parsed keybind enumerated from libghostty. A leader sequence
/// (e.g. Ctrl+K Ctrl+S) carries more than one step.
/// </summary>
public sealed record EnumeratedKeybind(
    IReadOnlyList<KeybindTrigger> Steps,
    string Action,
    GhosttyBindingFlags Flags);
