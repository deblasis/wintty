using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;

namespace Ghostty.Interop;

/// <summary>
/// Reads the full parsed keybind set from a libghostty config via the
/// count + indexed-getter C ABI and maps it to pure EnumeratedKeybind records.
/// </summary>
internal static class KeybindEnumerator
{
    public static IReadOnlyList<EnumeratedKeybind> Enumerate(GhosttyConfig config)
    {
        var count = NativeMethods.ConfigKeybindsCount(config);
        var result = new List<EnumeratedKeybind>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var native = NativeMethods.ConfigGetKeybind(config, i);
            result.Add(KeybindInterop.ToEnumerated(native));
        }

        return result;
    }
}
