using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Input;

/// <summary>Assign-time conflict lookups over the current (finalized) bind set.</summary>
public static class KeybindConflicts
{
    /// <summary>The existing binding whose trigger matches triggerToken, or null.</summary>
    public static EnumeratedKeybind? FindByTrigger(IReadOnlyList<EnumeratedKeybind> binds, string triggerToken)
    {
        var canonical = KeybindTriggerSyntax.Canonicalize(triggerToken);
        return binds.FirstOrDefault(b =>
            string.Equals(KeybindTriggerSyntax.Canonicalize(KeybindTriggerSyntax.Encode(b)), canonical,
                System.StringComparison.Ordinal));
    }
}
