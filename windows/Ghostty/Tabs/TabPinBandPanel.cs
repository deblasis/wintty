using System.Numerics;
using Ghostty.Core.Tabs;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace Ghostty.Tabs;

/// <summary>
/// The pinned zone's container: icon squares laid out in a wrapping band.
///
/// A StackPanel spent one row per pin. This spends one band row per
/// <see cref="TabPinBand.ColumnsFor"/> pins, which is the whole point of
/// the shape -- three pins cost one row in the expanded pane instead of
/// three -- and the change of shape is what marks the zone, so nothing
/// here draws a rule.
///
/// Children stay a flat list in manager order: the panel decides where a
/// square sits, never which squares exist or in what order, so the
/// strip's registry, its reconcile skew check and its arrow traversal all
/// keep reading <c>Children</c> as the pinned prefix.
/// </summary>
internal sealed partial class TabPinBandPanel : Panel
{
    /// <summary>
    /// Where each child was last arranged, so a re-arrange can tell a
    /// square that moved from one that did not. Keyed by element, not by
    /// index: an index moves under a child that never left its slot.
    /// </summary>
    private readonly Dictionary<UIElement, Point> _lastOrigin = new();

    /// <summary>Squares currently gliding, with the batch that hands them back.</summary>
    private readonly Dictionary<UIElement, CompositionScopedBatch> _gliding = new();

    private int _columns = 1;

    /// <summary>
    /// The width the PANE offered at the last measure.
    ///
    /// Kept because the arrange pass cannot re-derive it. The band is
    /// <see cref="HorizontalAlignment.Left"/>, so the size it is arranged at
    /// is the size it ASKED for -- the width of the squares it already holds
    /// -- and the column count is a fact about the pane, not about the
    /// content.
    /// </summary>
    private double _offeredWidth = double.NaN;

    /// <summary>
    /// Whether the band glides its squares between slots. Off collapses
    /// every reflow to a cut, the same contract the strip's other motion
    /// keeps: layout is correct before any animation runs, and a
    /// reduce-motion or High Contrast session sees the cut.
    ///
    /// Also the strip's hand-off switch. While a drag is live the drag
    /// owns every row's composition Translation -- its own glide pass
    /// offsets neighbours by their slot delta -- and two writers on one
    /// property is one writer too many, so the strip turns the band's
    /// reflow off for the length of the gesture.
    /// </summary>
    internal bool MotionEnabled { get; set; }

    /// <summary>The band's column count at the width it was last measured at.</summary>
    internal int Columns => _columns;

    /// <summary>
    /// The box the square at <paramref name="index"/> occupies, in this
    /// panel's own coordinates -- including the slot one past the end,
    /// which is what the drop preview promises. Read from the same
    /// arithmetic the arrange uses, so the ghost sits exactly where the
    /// real square will land and the hand-off does not flash.
    /// </summary>
    internal Rect SlotRect(int index)
    {
        var (x, y) = TabPinBand.OriginOf(index, _columns);
        return new Rect(x, y, TabPinBand.ChipSize, TabPinBand.ChipSize);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _offeredWidth = availableSize.Width;
        _columns = TabPinBand.ColumnsFor(availableSize.Width);
        var square = new Size(TabPinBand.ChipSize, TabPinBand.ChipSize);
        foreach (var child in Children) child.Measure(square);
        return new Size(
            TabPinBand.BandWidth(Children.Count, _columns),
            TabPinBand.BandHeight(Children.Count, _columns));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // From the offered width, NOT from finalSize. The band is
        // left-aligned, so the size it is arranged at is the size it asked
        // for -- the width of the squares it already holds -- and asking
        // ColumnsFor about that answers "as many columns as there are
        // squares".
        //
        // The squares themselves survived that, because with fewer squares
        // than columns every one of them is in row 0 either way. What did
        // not is SlotRect's slot one PAST the end, which is the only thing
        // the drop preview draws: with three pins in a pane that fits five,
        // _columns came back 3 and OriginOf(3, 3) is row 1, column 0. The
        // ghost promised a second band row, and the square landed beside
        // the last one -- the exact flash SlotRect's doc says reading the
        // arrange's own arithmetic prevents.
        _columns = TabPinBand.ColumnsFor(_offeredWidth);

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var slot = SlotRect(i);
            child.Arrange(slot);
            Reflow(child, new Point(slot.X, slot.Y));
        }

        // Children that left take their remembered slot with them, or a
        // square re-added later would glide in from wherever it used to
        // sit -- a slot the band may not even have any more.
        if (_lastOrigin.Count > Children.Count)
        {
            var live = new HashSet<UIElement>(Children);
            foreach (var gone in _lastOrigin.Keys.Where(c => !live.Contains(c)).ToList())
            {
                _lastOrigin.Remove(gone);
                HandBack(gone);
            }
        }

        return new Size(
            TabPinBand.BandWidth(Children.Count, _columns),
            TabPinBand.BandHeight(Children.Count, _columns));
    }

    /// <summary>
    /// One square's move to a new slot: pin it visually where its old
    /// slot was (composition Translation carries the negative delta
    /// against the arrange that just happened) and ease it home to zero.
    ///
    /// Both axes, unlike the list's glide: a band wraps, so the pin that
    /// a new neighbour pushes off the end of a row travels down AND back
    /// to the left, and a vertical-only glide would slide it through the
    /// squares it is passing.
    ///
    /// A square arranged for the first time only records its slot. It has
    /// no old slot to come from, and gliding it from the band's origin
    /// would fly every pin in from the corner on the strip's first frame.
    /// </summary>
    private void Reflow(UIElement child, Point origin)
    {
        if (!_lastOrigin.TryGetValue(child, out var was))
        {
            _lastOrigin[child] = origin;
            return;
        }
        _lastOrigin[child] = origin;

        var delta = new Vector3(
            (float)(was.X - origin.X), (float)(was.Y - origin.Y), 0);
        if (!MotionEnabled || delta.LengthSquared() < 0.25f)
        {
            // A cut still owes the hand-back: a square that stops
            // gliding mid-flight keeps whatever Translation it held.
            HandBack(child);
            return;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(child);
            // A square already gliding is stopped first, so it takes the
            // fresh delta from where it is rather than composing two
            // half-finished glides -- the list's own chained-flick rule.
            visual.StopAnimation("Translation");
            ElementCompositionPreview.SetIsTranslationEnabled(child, true);
            visual.Properties.InsertVector3("Translation", delta);

            var glide = visual.Compositor.CreateVector3KeyFrameAnimation();
            glide.Duration = TimeSpan.FromMilliseconds(TabStripMotion.GapGlideMs);
            glide.InsertKeyFrame(1f, Vector3.Zero);

            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _gliding[child] = batch;
            batch.Completed += (_, _) =>
            {
                // Only a square still riding THIS batch is handed back: a
                // re-glide inside the window replaced it, and the batch it
                // superseded must not strip the new one's Translation.
                if (_gliding.TryGetValue(child, out var live)
                    && ReferenceEquals(live, batch))
                    HandBack(child);
            };
            visual.StartAnimation("Translation", glide);
            batch.End();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // Composition refused: this square lands as a cut, like the
            // motion-off path. Layout never depends on the glide.
            _gliding.Remove(child);
        }
    }

    /// <summary>
    /// Give a square its Translation back, so nothing the band did to it
    /// survives the glide that borrowed it.
    /// </summary>
    private void HandBack(UIElement child)
    {
        if (!_gliding.Remove(child)) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(child);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            ElementCompositionPreview.SetIsTranslationEnabled(child, false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // The element is leaving the tree; its visual goes with it.
        }
    }

    /// <summary>
    /// Stop every glide and hand every square back. The strip calls this
    /// on teardown and whenever a gesture takes over the rows' Translation,
    /// so a band animation can never outlive the pass that started it.
    /// </summary>
    internal void StopMotion()
    {
        foreach (var child in _gliding.Keys.ToList()) HandBack(child);
        _gliding.Clear();
    }
}
