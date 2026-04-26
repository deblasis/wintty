using System.Collections.Generic;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Input;

/// <summary>
/// Pure-logic helper that maps a 1-based slot index to a profile id from
/// the live registry snapshot. Returns null for out-of-range, empty, or
/// non-positive inputs. Single source of truth for the
/// Ctrl+Shift+1..9 chord behavior; PaneActionRouter in the WinUI
/// assembly is a thin dispatcher that delegates here so the behavior is
/// testable from Ghostty.Tests.
/// </summary>
public static class ProfileSlotResolver
{
    /// <summary>
    /// Return the id of the profile at 1-based <paramref name="slot"/>
    /// in <paramref name="profiles"/>, or null when the slot is out of
    /// range or non-positive. The list is expected to already be
    /// hidden-filtered and ordered (per ProfileOrderResolver).
    /// </summary>
    public static string? Resolve(IReadOnlyList<ResolvedProfile> profiles, int slot)
    {
        if (slot < 1) return null;
        var index = slot - 1;
        if (index >= profiles.Count) return null;
        return profiles[index].Id;
    }
}
