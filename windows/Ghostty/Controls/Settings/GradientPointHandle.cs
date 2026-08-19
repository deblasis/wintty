using System;
using Ghostty.Accessibility;
using Ghostty.Core.Settings;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Controls.Settings;

/// <summary>
/// One draggable point on the gradient canvas.
///
/// A plain <see cref="ContentControl"/> is still the right base: Button's
/// internal pointer template steals PointerPressed before the editor's drag
/// handler can run. But a bare ContentControl produces no automation element
/// at all, so the handles were absent from the UIA tree entirely - focusable
/// by keyboard, and invisible to anything reading the window. This subclass
/// exists to give them a peer.
/// </summary>
internal sealed partial class GradientPointHandle : ContentControl
{
    private GradientPointHandleAutomationPeer? _peer;

    /// <summary>Zero-based position of this point in the editor's list.</summary>
    internal int Index { get; set; }

    /// <summary>How many points the editor currently has.</summary>
    internal int Total { get; set; }

    /// <summary>Normalized 0..1 position across the canvas.</summary>
    internal float X { get; set; }

    /// <summary>Normalized 0..1 position down the canvas.</summary>
    internal float Y { get; set; }

    /// <summary>
    /// Asked when a client requests a new position through the Value
    /// pattern. Coordinates are normalized and clamped. Returns false when
    /// the editor refuses - the handle is no longer on the canvas, or a
    /// drag is in flight - so the peer can tell the client rather than
    /// return a success it did not deliver.
    /// </summary>
    internal Func<GradientPointHandle, float, float, bool>? PositionRequested;

    /// <summary>What a client hears when focus lands here.</summary>
    internal string AccessibleName => GradientPointsLogic.DescribeHandle(Index, Total);

    /// <summary>The position, spoken rather than plotted.</summary>
    internal string PositionText => GradientPointsLogic.DescribePosition(X, Y);

    internal bool RequestPosition(float x, float y) =>
        PositionRequested?.Invoke(this, x, y) ?? false;

    /// <summary>
    /// Whether a peer has ever been asked for. Formatting a position costs
    /// a string, and the drag path runs this per pointer sample.
    /// </summary>
    internal bool HasAutomationPeer => _peer is not null;

    /// <summary>
    /// Tell any listening client the point moved. Called after a drag, an
    /// arrow key, or a client's own SetValue, so all three routes announce.
    /// </summary>
    internal void NotifyPositionChanged(string oldPosition) =>
        _peer?.NotifyValueChanged(oldPosition, PositionText);

    protected override AutomationPeer OnCreateAutomationPeer()
        => _peer ??= new GradientPointHandleAutomationPeer(this);
}
