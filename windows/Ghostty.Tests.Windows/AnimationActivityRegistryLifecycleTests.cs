using System;
using System.Threading.Tasks;
using Ghostty.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    public async Task AForeverCompositionEntry_StandsUntilItIsReleased()
    {
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
}
