using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ghostty.Motion;

/// <summary>
/// The one place every animation start in the shell routes through, and the
/// bookkeeping that lets the shell answer, deterministically, "is anything
/// animating on this element and property right now".
///
/// Each helper records the (element, property) pair and then performs the
/// start itself, so a routed site reads exactly as it did before routing:
/// the helpers call through immediately and change no animation's behaviour.
///
/// Entries leave the registry three ways:
///
/// - a Storyboard's <c>Completed</c> (WinUI 3 raises it from <c>Stop</c> as
///   well as from a natural finish, so both paths land here; the
///   subscription removes itself when it fires, so a reused board
///   accumulates nothing);
/// - the element's <c>Unloaded</c>, which the registry subscribes to itself
///   the first time an element registers -- and <see cref="OnElementUnloaded"/>
///   for owners that tear an element's motion down explicitly (a disposed
///   pane decoration, a strip teardown);
/// - an explicit release for composition starts that carry no element
///   (<see cref="ReleaseCompositionAnimation"/>) -- an
///   <c>IterationBehavior.Forever</c> animation never Completes, so without
///   a stop or an unload its entry would stand forever.
///
/// The bookkeeping is weak everywhere (two
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>s, keyed on the element
/// and on the composition target), so a pane that dies without an unload
/// still stops counting: the registry holds nothing alive, and dead-pane
/// accumulation is impossible by construction.
///
/// Not every start can be keyed on a <see cref="UIElement"/>: a Storyboard
/// that drives a Brush, and a composition animation that drives a
/// <see cref="CompositionPropertySet"/> or an ownerless clip, have no
/// element at the site. Those register under the object they drive; they
/// count toward <see cref="TotalActive"/> and leave at Completed or release.
///
/// The registration members take the element as the <see cref="UIElement"/>
/// the animation actually targets. Unloaded is a FrameworkElement event in
/// WinUI 3, so only elements that are one get the self-maintaining unload
/// subscription; the rest leave at their Completed, their explicit release,
/// or the death of the object they are keyed on -- and because
/// <see cref="TotalActive"/> is summed from the weak tables, none of those
/// paths can leak a count.
/// </summary>
internal static class AnimationActivityRegistry
{
    /// <summary>Per-element counts, keyed weakly. The contract's table.</summary>
    private static readonly ConditionalWeakTable<UIElement, ElementState> ByElement = new();

    /// <summary>
    /// Counts whose site had no element: the brush a Storyboard paints
    /// through, the property set a timeline's drivers run on, a clip that
    /// reaches the site ownerless. Keyed weakly on the driven object.
    /// </summary>
    private static readonly ConditionalWeakTable<object, Dictionary<string, int>> ByObject = new();

    /// <summary>
    /// Release lookup for element-keyed composition starts: (composition
    /// target, property) -> the element whose count to drop. A release
    /// arrives as the pair a site started with, but the count lives on the
    /// element; this is the bridge. Weak on the target, so a released
    /// composition object leaves nothing behind.
    /// </summary>
    private static readonly ConditionalWeakTable<CompositionObject, Dictionary<string, UIElement>>
        ReleaseIndex = new();

    /// <summary>Elements already subscribed to Unloaded.</summary>
    private static readonly ConditionalWeakTable<UIElement, object> Hooked = new();

    private static readonly object Gate = new();

    private sealed class ElementState
    {
        public readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);
    }

    // -- Starts -------------------------------------------------------------

    /// <summary>
    /// Begin <paramref name="storyboard"/> on <paramref name="element"/>,
    /// counting the (element, property) activity until the storyboard
    /// Completes or the element Unloads.
    /// </summary>
    internal static void BeginStoryboard(Storyboard storyboard, UIElement element, string property)
    {
        var release = Increment(element, property);
        SubscribeCompleted(storyboard, release);
        storyboard.Begin();
    }

    /// <summary>
    /// Begin <paramref name="storyboard"/> on <paramref name="brush"/>. A
    /// brush has no Unloaded; the entry leaves at Completed (WinUI raises it
    /// from Stop too, which is the path a superseded flight takes).
    /// </summary>
    internal static void BeginStoryboard(Storyboard storyboard, Brush brush, string property)
    {
        var release = IncrementObject(brush, property);
        SubscribeCompleted(storyboard, release);
        storyboard.Begin();
    }

    /// <summary>
    /// Begin <paramref name="storyboard"/> at a site with neither element
    /// nor brush to key on: a board that drives several cards at once, one
    /// of which may already be leaving the tree. The entry is keyed on the
    /// storyboard itself and leaves at its Completed -- which WinUI raises
    /// from a Stop as well, so the next move's land ends this one's entry.
    /// </summary>
    internal static void BeginStoryboard(Storyboard storyboard, string property)
    {
        var release = IncrementObject(storyboard, property);
        SubscribeCompleted(storyboard, release);
        storyboard.Begin();
    }

    /// <summary>
    /// Subscribe <paramref name="storyboard"/>'s Completed to
    /// <paramref name="release"/>, once. The handler is the self-removing
    /// kind: it holds its own name, and its first act is to unsubscribe
    /// itself. A handler that stayed subscribed would outlive its fire on a
    /// REUSED board -- the switcher popup's two field boards run for the
    /// popup's whole life -- and the closure's hold on the target would
    /// root one element or brush per use, for the board's life. The
    /// registry's law is that it holds nothing alive, so the subscription
    /// dies with the one fire it exists for.
    /// </summary>
    private static void SubscribeCompleted(Storyboard storyboard, Action release)
    {
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            storyboard.Completed -= handler;
            release();
        };
        storyboard.Completed += handler;
    }

    /// <summary>
    /// Start <paramref name="animation"/> on <paramref name="target"/>,
    /// counting the activity against <paramref name="element"/>, whose
    /// Unloaded (or an explicit <see cref="OnElementUnloaded"/>) ends it.
    /// </summary>
    internal static void StartCompositionAnimation(
        CompositionObject target, UIElement element, string property, CompositionAnimation animation)
    {
        Increment(element, property);
        lock (Gate)
        {
            var index = ReleaseIndex.GetOrCreateValue(target);
            index[property] = element;
        }

        target.StartAnimation(property, animation);
    }

    /// <summary>
    /// Start <paramref name="animation"/> on <paramref name="target"/> when
    /// the site has no element to key on: the count lives on the target and
    /// leaves at <see cref="ReleaseCompositionAnimation"/>. A Forever
    /// animation's only exits are this and the target's own death.
    /// </summary>
    internal static void StartCompositionAnimation(
        CompositionObject target, string property, CompositionAnimation animation)
    {
        IncrementObject(target, property);
        target.StartAnimation(property, animation);
    }

    /// <summary>
    /// Drop the entry a no-element <see cref="StartCompositionAnimation"/>
    /// (or an element-keyed one, via the release index) stands under. Called
    /// where the site stops the animation; a release for an entry that is
    /// already gone is a no-op, not an error.
    /// </summary>
    internal static void ReleaseCompositionAnimation(CompositionObject target, string property)
    {
        lock (Gate)
        {
            if (ReleaseIndex.TryGetValue(target, out var index)
                && index.Remove(property, out var element))
            {
                Decrement(element, property);
                return;
            }

            if (ByObject.TryGetValue(target, out var counts)
                && counts.TryGetValue(property, out var n))
            {
                if (n <= 1) counts.Remove(property); else counts[property] = n - 1;
            }
        }
    }

    /// <summary>
    /// Drop every entry standing against <paramref name="element"/>. The
    /// Unloaded subscription calls this; owners that tear an element's
    /// motion down before its Unloaded (a disposed decoration, a strip
    /// teardown) call it directly. Idempotent.
    /// </summary>
    internal static void OnElementUnloaded(UIElement element)
    {
        lock (Gate)
        {
            ByElement.Remove(element);
        }
    }

    // -- Queries ------------------------------------------------------------

    /// <summary>Whether anything is animating <paramref name="property"/> on
    /// <paramref name="element"/> right now.</summary>
    internal static int ActiveCount(UIElement element, string property)
    {
        lock (Gate)
        {
            return ByElement.TryGetValue(element, out var state)
                && state.Counts.TryGetValue(property, out var n)
                    ? n
                    : 0;
        }
    }

    /// <summary>Every entry the registry stands behind, element-keyed and
    /// object-keyed together. Summed from the two weak tables on demand
    /// rather than kept as a counter, so an element that dies without its
    /// Unloaded -- possible for a bare UIElement, which has no Unloaded to
    /// hook -- cannot leak a count into the total. Both tables are small:
    /// one row per element or object with live activity.</summary>
    internal static int TotalActive
    {
        get
        {
            lock (Gate)
            {
                var total = 0;
                foreach (var row in ByElement) total += TotalOf(row.Value.Counts);
                foreach (var row in ByObject) total += TotalOf(row.Value);
                return total;
            }
        }
    }

    // -- Bookkeeping ----------------------------------------------------------

    private static Action Increment(UIElement element, string property)
    {
        var state = ByElement.GetOrCreateValue(element);
        Hook(element);
        lock (Gate)
        {
            state.Counts.TryGetValue(property, out var n);
            state.Counts[property] = n + 1;
        }

        return () => Decrement(element, property);
    }

    private static Action IncrementObject(object target, string property)
    {
        var counts = ByObject.GetOrCreateValue(target);
        lock (Gate)
        {
            counts.TryGetValue(property, out var n);
            counts[property] = n + 1;
        }

        return () =>
        {
            lock (Gate)
            {
                if (!ByObject.TryGetValue(target, out var live)
                    || !live.TryGetValue(property, out var m))
                {
                    return;
                }

                if (m <= 1) live.Remove(property); else live[property] = m - 1;
            }
        };
    }

    private static void Decrement(UIElement element, string property)
    {
        lock (Gate)
        {
            if (!ByElement.TryGetValue(element, out var state)
                || !state.Counts.TryGetValue(property, out var n))
            {
                return;
            }

            if (n <= 1) state.Counts.Remove(property); else state.Counts[property] = n - 1;
        }
    }

    /// <summary>
    /// Subscribe once per element, the first time anything registers against
    /// it. The subscription is what makes the Unloaded removal
    /// self-maintaining: a site never has to remember to clean up, and an
    /// element that re-loads re-registers its activity fresh.
    /// </summary>
    private static void Hook(UIElement element)
    {
        // Unloaded is a FrameworkElement event in WinUI 3; a bare UIElement
        // offers no unload to subscribe to. Its entries then leave only
        // through an explicit OnElementUnloaded or the target's own death --
        // TotalActive sums the weak tables, so even that path cannot leak.
        if (element is not FrameworkElement frameworkElement) return;

        lock (Gate)
        {
            if (Hooked.TryGetValue(element, out _)) return;
            Hooked.Add(element, element);
        }

        frameworkElement.Unloaded += (_, _) => OnElementUnloaded(element);
    }

    private static int TotalOf(Dictionary<string, int> counts)
    {
        var sum = 0;
        foreach (var n in counts.Values) sum += n;
        return sum;
    }
}
