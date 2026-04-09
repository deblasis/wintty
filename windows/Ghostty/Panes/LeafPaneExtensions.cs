using Ghostty.Controls;
using Ghostty.Core.Panes;

namespace Ghostty.Panes;

/// <summary>
/// WinUI-side helpers that interpret <see cref="LeafPane.Tag"/> as
/// its real type (<see cref="TerminalControl"/>). Concentrates the
/// unchecked cast in exactly one place; every other call site just
/// writes <c>leaf.Terminal()</c>.
///
/// <para>Why this exists: <see cref="LeafPane"/> lives in
/// <c>Ghostty.Core</c>, which is a plain net9.0 assembly that cannot
/// reference WinAppSDK. So the terminal pointer is stashed in the
/// opaque <see cref="LeafPane.Tag"/> slot when <c>PaneHost</c>
/// creates the leaf, and retrieved here. Partial classes across
/// assemblies do not work in C# (all parts must live in one
/// assembly) — this extension is the minimal alternative.</para>
/// </summary>
internal static class LeafPaneExtensions
{
    /// <summary>
    /// Return the <see cref="TerminalControl"/> attached to this
    /// leaf. Throws <see cref="System.InvalidCastException"/> if
    /// <see cref="LeafPane.Tag"/> is null or the wrong type, which
    /// would indicate a <c>PaneHost</c> bug, not a user error.
    /// </summary>
    public static TerminalControl Terminal(this LeafPane leaf) =>
        (TerminalControl)leaf.Tag!;
}
