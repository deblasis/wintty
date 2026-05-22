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
            // Safe fallback — Core's enum is closed and the switch is
            // exhaustive today, but a future MouseShapeFamily addition
            // mustn't crash the input pipeline.
            _                                       => InputSystemCursorShape.Arrow,
        };
}
