using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ghostty.Core.Input;

namespace Ghostty.Core.Interop;

/// <summary>Bitfield mirror of ghostty_binding_flags_e.</summary>
[Flags]
public enum GhosttyBindingFlags : uint
{
    None = 0,
    Consumed = 1 << 0,
    All = 1 << 1,
    Global = 1 << 2,
    Performable = 1 << 3,
}

/// <summary>Blittable mirror of ghostty_input_trigger_s.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GhosttyTriggerC
{
    public int Tag;   // 0=physical, 1=unicode, 2=catch_all
    public uint Key;  // union: translated/physical key or unicode codepoint
    public uint Mods; // modifier flags
}

/// <summary>Inline fixed buffer of GHOSTTY_KEYBIND_MAX_STEPS triggers.</summary>
[InlineArray(KeybindInterop.MaxSteps)]
public struct TriggerSteps
{
    private GhosttyTriggerC _element0;
}

/// <summary>Blittable mirror of ghostty_keybind_s.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GhosttyKeybindC
{
    public TriggerSteps Steps;
    public uint StepCount;
    public IntPtr Action; // const char*, owned by config
    public uint Flags;
}

public static class KeybindInterop
{
    /// <summary>Keep in sync with GHOSTTY_KEYBIND_MAX_STEPS and keybind_max_steps.</summary>
    public const int MaxSteps = 4;

    /// <summary>Maps a native keybind struct to the pure record (decodes the action string).</summary>
    public static EnumeratedKeybind ToEnumerated(in GhosttyKeybindC c)
    {
        var count = (int)Math.Min(c.StepCount, (uint)MaxSteps);
        var steps = new List<KeybindTrigger>(count);

        // InlineArray's indexer requires a writable ref, which an `in`
        // parameter forbids, so copy the (small, cold-path) buffer locally.
        var stepBuffer = c.Steps;
        for (var i = 0; i < count; i++)
        {
            var t = stepBuffer[i];
            steps.Add(new KeybindTrigger(t.Tag, t.Key, t.Mods));
        }

        var action = c.Action == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUTF8(c.Action) ?? string.Empty;

        return new EnumeratedKeybind(steps, action, (GhosttyBindingFlags)c.Flags);
    }
}
