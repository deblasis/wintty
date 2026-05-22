using System.Runtime.CompilerServices;
using Ghostty.Core.Input;
using Microsoft.UI.Input;

namespace Ghostty.Controls;

/// <summary>
/// Trivial 1:1 adapter from <see cref="MouseShapeFamily"/> (defined in
/// Ghostty.Core so the 34-shape mapping table is unit-testable without
/// WinAppSDK) to WinUI's <see cref="InputSystemCursorShape"/>. Smoke-
/// tested only — the mapping is small and the WinUI enum values are
/// stable across SDK versions.
/// </summary>
internal static class MouseShapeAdapter
{
    /// <summary>
    /// Switch over the 13 named <see cref="MouseShapeFamily"/> values
    /// with a throwing wildcard. Silent degradation to <c>Arrow</c>
    /// would mask either an upstream-enum drift (caller passing
    /// <c>(MouseShapeFamily)999</c>) or a forgotten case after a new
    /// family is added. C# can't express "exhaustive over named enum
    /// members only" — enums logically contain every underlying int,
    /// so CS8524 fires without a wildcard. Throwing on the wildcard
    /// gives the next-best contract: runtime explosion on unknowns,
    /// every named member explicitly handled in the diff.
    /// </summary>
    internal static InputSystemCursorShape ToWinUI(MouseShapeFamily family) =>
        family switch
        {
            MouseShapeFamily.Arrow                  => InputSystemCursorShape.Arrow,
            MouseShapeFamily.Hand                   => InputSystemCursorShape.Hand,
            MouseShapeFamily.IBeam                  => InputSystemCursorShape.IBeam,
            MouseShapeFamily.Wait                   => InputSystemCursorShape.Wait,
            MouseShapeFamily.AppStarting            => InputSystemCursorShape.AppStarting,
            MouseShapeFamily.Cross                  => InputSystemCursorShape.Cross,
            MouseShapeFamily.Help                   => InputSystemCursorShape.Help,
            MouseShapeFamily.SizeAll                => InputSystemCursorShape.SizeAll,
            MouseShapeFamily.UniversalNo            => InputSystemCursorShape.UniversalNo,
            MouseShapeFamily.SizeWestEast           => InputSystemCursorShape.SizeWestEast,
            MouseShapeFamily.SizeNorthSouth         => InputSystemCursorShape.SizeNorthSouth,
            MouseShapeFamily.SizeNortheastSouthwest => InputSystemCursorShape.SizeNortheastSouthwest,
            MouseShapeFamily.SizeNorthwestSoutheast => InputSystemCursorShape.SizeNorthwestSoutheast,
            _                                       => throw new SwitchExpressionException(family),
        };
}
