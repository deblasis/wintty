using System;
using System.Threading.Tasks;
using Ghostty.Motion;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// The animation-activity registry's lifecycle, against the real runtime:
/// the exits an entry actually has. Every other guarantee the registry
/// makes is bookkeeping over these, so these are the tests that can catch
/// a real drop failure -- the census proves the shell routes its starts;
/// these prove a routed start's entry leaves again.
///
/// They need the platform (a Storyboard that raises Completed, an element
/// visual, a Forever iteration) and no window: the objects are created on
/// the probe's dedicated XAML thread and never enter a visual tree, which
/// the registry's contract never requires -- entries are keyed on
/// elements, not on windows.
///
/// Deliberately absent: a storyboard left to finish NATURALLY. Its clock
/// is the renderer's, and how an unattached storyboard ticks is a fact
/// about the host's rendering pipeline, not about the registry -- a
/// wall-clock wait on it is exactly the load-sensitive test this suite
/// does not carry. The Completed path is exercised through the stop that
/// raises it, which is the documented, deterministic raise, and the same
/// handler the natural finish would run.
/// </summary>
public class AnimationActivityRegistryLifecycleTests
{
    private static readonly TimeSpan ExitCap = TimeSpan.FromSeconds(30);

    /// <summary>Await a signal the XAML thread sets, under the hang cap.</summary>
    private static async Task<T> Signal<T>(TaskCompletionSource<T> signal)
    {
        var done = await Task.WhenAny(signal.Task, Task.Delay(ExitCap)).ConfigureAwait(false);
        if (done != signal.Task)
        {
            throw new TimeoutException(
                "the registry exit never signalled within the cap; a wedged runtime is a host fault, not a test verdict");
        }

        return await signal.Task.ConfigureAwait(false);
    }

    [XamlFact]
    public async Task AStoppedStoryboard_RaisesCompleted_AndDropsItsEntry()
    {
        var host = new Grid();
        var completed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;

        await XamlProbe.OnDispatcherAsync<object?>(() =>
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMinutes(10)),
            };
            Storyboard.SetTarget(fade, host);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            AnimationActivityRegistry.BeginStoryboard(storyboard, host, "Opacity");
            Assert.Equal(1, AnimationActivityRegistry.ActiveCount(host, "Opacity"));

            storyboard.Completed += (_, _) =>
            {
                observed = AnimationActivityRegistry.ActiveCount(host, "Opacity");
                completed.TrySetResult(null);
            };

            // WinUI 3 raises Completed from Stop as well as from a natural
            // finish -- the documented raise the registry's Completed exit
            // leans on (it is the path a superseded flight takes).
            storyboard.Stop();
            return null;
        });

        await Signal(completed);
        Assert.Equal(0, observed);
        Assert.Equal(0,
            await XamlProbe.OnDispatcherAsync(() => AnimationActivityRegistry.ActiveCount(host, "Opacity")));
    }

    [XamlFact]
    public async Task AnUnload_DropsEveryEntry_AtOnce()
    {
        var host = new Grid();

        await XamlProbe.OnDispatcherAsync<object?>(() =>
        {
            // A duration the test cannot outwait is the point: the only way
            // this entry leaves is the unload, which is what is under test.
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMinutes(10)),
            };
            Storyboard.SetTarget(fade, host);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            AnimationActivityRegistry.BeginStoryboard(storyboard, host, "Opacity");
            Assert.Equal(1, AnimationActivityRegistry.ActiveCount(host, "Opacity"));

            AnimationActivityRegistry.OnElementUnloaded(host);
            Assert.Equal(0, AnimationActivityRegistry.ActiveCount(host, "Opacity"));

            storyboard.Stop();
            return null;
        });
    }

    [XamlCompositionFact]
    public async Task AForeverCompositionEntry_StandsUntilItIsReleased()    {
        var host = new Grid();

        await XamlProbe.OnDispatcherAsync<object?>(() =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(host);
            var compositor = visual.Compositor;
            var spin = compositor.CreateScalarKeyFrameAnimation();
            spin.InsertKeyFrame(1f, 1f);
            spin.Duration = TimeSpan.FromMilliseconds(100);
            spin.IterationBehavior = AnimationIterationBehavior.Forever;

            // No release call exists for this shape of start in the test:
            // a Forever animation never Completes, so the entry must still
            // be standing while the animation runs -- the case a Completed
            // -only exit would silently lose.
            AnimationActivityRegistry.StartCompositionAnimation(visual, host, "Scale", spin);
            Assert.Equal(1, AnimationActivityRegistry.ActiveCount(host, "Scale"));
            Assert.True(
                AnimationActivityRegistry.TotalActive >= 1,
                "the Forever entry must count toward the total");

            AnimationActivityRegistry.OnElementUnloaded(host);
            Assert.Equal(0, AnimationActivityRegistry.ActiveCount(host, "Scale"));

            visual.StopAnimation("Scale");
            return null;
        });
    }

    [XamlDependencyFact]
    public async Task AReusedStoryboard_DoesNotRootATargetItFinished()
    {
        // The switcher popup's two field boards are fields: they run for the
        // popup's whole life and drive a different card or brush per use. A
        // Completed handler that stays subscribed after it fires keeps every
        // past use's target rooted through the board -- the exact law the
        // registry's class doc states it holds nothing alive. The entry
        // counts cannot see this (a second release is a no-op), so the pin
        // is the ROOTING itself: after the first target is finished and the
        // board has moved to a second one, only the registry's subscription
        // could still be holding the first target, and it must be gone.
        var board = default(Storyboard);
        var firstRef = default(WeakReference);
        var completed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var afterFirstStop = 0;

        await XamlProbe.OnDispatcherAsync<object?>(() =>
        {
            board = new Storyboard();

            // First use: a brush target, the ActiveFieldFill shape.
            var first = new SolidColorBrush(Colors.Black);
            firstRef = new WeakReference(first);
            var anim1 = new ColorAnimation
            {
                From = Colors.Black,
                To = Colors.Gray,
                Duration = new Duration(TimeSpan.FromMinutes(10)),
            };
            Storyboard.SetTarget(anim1, first);
            Storyboard.SetTargetProperty(anim1, "Color");
            board.Children.Add(anim1);

            var before = AnimationActivityRegistry.TotalActive;
            AnimationActivityRegistry.BeginStoryboard(board, first, "Color");
            Assert.Equal(before + 1, AnimationActivityRegistry.TotalActive);

            board.Completed += (_, _) => completed.TrySetResult(null);
            board.Stop();
            return null;
        });

        await Signal(completed);
        afterFirstStop = await XamlProbe.OnDispatcherAsync(
            () => AnimationActivityRegistry.TotalActive);
        Assert.Equal(0, afterFirstStop);

        // Second use: the popup's own sequence -- clear the old children
        // (which drops the timeline's strong hold on the first target),
        // target a new one, reuse the same board.
        var secondSignal = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await XamlProbe.OnDispatcherAsync<object?>(() =>
        {
            board!.Children.Clear();
            var second = new SolidColorBrush(Colors.White);
            var anim2 = new ColorAnimation
            {
                From = Colors.White,
                To = Colors.Gray,
                Duration = new Duration(TimeSpan.FromMinutes(10)),
            };
            Storyboard.SetTarget(anim2, second);
            Storyboard.SetTargetProperty(anim2, "Color");
            board.Children.Add(anim2);

            var before = AnimationActivityRegistry.TotalActive;
            AnimationActivityRegistry.BeginStoryboard(board, second, "Color");
            Assert.Equal(before + 1, AnimationActivityRegistry.TotalActive);
            board.Completed += (_, _) => secondSignal.TrySetResult(null);
            board.Stop();
            return null;
        });

        await Signal(secondSignal);

        // The pin. With the subscription removed at its own Completed,
        // nothing in the process holds the first target: two blocking
        // collects around finalization settle every reachable reference.
        // With a handler that stays subscribed, the board -- alive for the
        // whole test -- holds the release closure, the closure holds the
        // first target, and the first WeakReference stays alive.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.False(
            firstRef!.IsAlive,
            "the registry's Completed subscription is still rooting the target "
            + "of a storyboard run that already finished");
    }
}
