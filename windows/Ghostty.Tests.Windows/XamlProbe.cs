using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using DynamicDependency = Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Answers "can this host bring the WinUI 3 runtime up at all, for the
/// kind of object a lifecycle test needs?". The animation registry's
/// lifecycle tests run real Storyboards and real composition animations,
/// and some hosts cannot: the runtime deployment may be absent or broken,
/// process creation for the runtime activation may be refused, or a wedged
/// machine may never answer the dispatcher. A test that reported a registry
/// failure there would be reporting the machine, not the subject.
///
/// The probe deliberately drives the PLATFORM itself -- a Grid, a
/// Storyboard, an element visual -- and never the code under test. If it
/// used AnimationActivityRegistry, a regression there would fail the probe,
/// the lifecycle tests would skip, and the regression would ship unnoticed.
/// On an independent path the two causes stay separable: environment broken
/// means skip, registry broken means the tests still run and fail.
///
/// There are two facets because the answer is not one answer. A host whose
/// composition pipeline will not come up can still run Storyboards, and
/// gating the storyboard tests on the composition probe would shrink the
/// suite by more than the machine forces. Each test gates on the facet it
/// actually exercises.
///
/// The runtime lives on one dedicated thread with a DispatcherQueue for
/// the lifetime of the test host: XAML objects carry thread affinity, and
/// the xunit worker threads have none. The bootstrap creates that thread
/// once and everything afterwards -- probe dry runs included -- is enqueued
/// to it, so discovery (where xunit reads Skip off the attribute) reaches
/// the same verdict the run does.
/// </summary>
internal sealed class XamlProbe
{
    /// <summary>
    /// Hard cap on one dispatcher turn, the same role SpawnProbe's cap
    /// plays: a hang guard so a wedged runtime cannot hang the suite, not
    /// a latency requirement. A healthy turn answers in milliseconds; a
    /// host merely SLOW pays nothing, because nothing here re-tries or
    /// measures -- one turn, decided.
    /// </summary>
    private static readonly TimeSpan TurnCap = TimeSpan.FromSeconds(60);

    /// <summary>The bootstrap thread's queue; null only when the bootstrap failed.</summary>
    private static DispatcherQueue? Queue;

    /// <summary>Why the runtime could not be brought up at all, if it could not.</summary>
    private static Exception? BootstrapFailure;

    /// <summary>The two facet probes. Created in the static constructor AFTER
    /// the bootstrap, in its body rather than as field initializers: an
    /// initializer would run before the bootstrap and see a half-initialized
    /// type -- no queue, no recorded failure -- and decide from a state that
    /// cannot exist once initialization completes.</summary>
    internal static readonly XamlProbe Storyboard;
    internal static readonly XamlProbe Composition;

    /// <summary>
    /// The probe's own cap. A dry run answers in milliseconds when it
    /// answers at all; this exists so a host where XAML object creation
    /// HANGS -- as bare test hosts can -- pays seconds once, not the full
    /// work cap, per probe.
    /// </summary>
    private static readonly TimeSpan ProbeCap = TimeSpan.FromSeconds(15);

    static XamlProbe()
    {
        Bootstrap();
        Storyboard = new XamlProbe(StoryboardDryRun, ProbeCap);
        // Both dry runs begin with XAML object creation, so a host that
        // hangs there fails the storyboard probe first. Rather than hang
        // again for the composition probe, it inherits that verdict: a
        // host whose verdict is already "skip" never runs a composition
        // test, so no composition claim ever rests on the placeholder.
        Composition = Storyboard.Unavailable is null
            ? new XamlProbe(CompositionDryRun, ProbeCap)
            : Storyboard;
    }

    private XamlProbe(Action dryRun, TimeSpan cap)
    {
        if (BootstrapFailure is { } failure)
        {
            Unavailable = "the WinUI runtime could not be brought up on this host: " + failure.Message;
            return;
        }

        try
        {
            OnDispatcherAsync<object?>(() =>
                {
                    dryRun();
                    return null;
                }, cap)
                .GetAwaiter().GetResult();
            Unavailable = null;
        }
        catch (Exception ex)
        {
            Unavailable = "the WinUI runtime came up but the probe's dry run failed on this host: " + ex.Message;
        }
    }

    /// <summary>
    /// The Storyboard facet's dry run: creates XAML objects and drives the
    /// storyboard API surface the lifecycle tests use, with no timing and
    /// no composition.
    /// </summary>
    private static void StoryboardDryRun()
    {
        _ = new Grid();
        var storyboard = new Storyboard();
        storyboard.Stop();
    }

    /// <summary>
    /// The composition facet's dry run: an element visual from an
    /// unattached element, started against and stopped, which is everything
    /// the composition lifecycle test asks the platform for.
    /// </summary>
    private static void CompositionDryRun()
    {
        var host = new Grid();
        var visual = ElementCompositionPreview.GetElementVisual(host);
        visual.StopAnimation("Opacity");
        _ = visual.Compositor;
    }

    /// <summary>Why the gated tests skip on this host, or null to run.</summary>
    internal string? Unavailable { get; }

    private static void Bootstrap()
    {
        // A bare test host has no WinAppSDK initialization of its own: the
        // app's startup initializes the runtime's dynamic dependency, and
        // until something does, every Microsoft.UI.* activation factory is
        // simply absent ("ClassFactory cannot supply requested class").
        // Best effort, recorded: some deployments activate without it, and
        // the dry run below is the verdict that counts either way.
        try
        {
            if (!DynamicDependency.Bootstrap.TryInitialize(0x00020000, out var hr))
            {
                BootstrapFailure = new InvalidOperationException(
                    "WinAppSDK dynamic-dependency bootstrap refused (hr=" + hr + ")");
            }
        }
        catch (Exception ex)
        {
            BootstrapFailure = new InvalidOperationException(
                "WinAppSDK dynamic-dependency bootstrap could not be attempted: " + ex.Message);
        }

        var ready = new TaskCompletionSource<DispatcherQueue>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var controller = DispatcherQueueController.CreateOnDedicatedThread();
                ready.SetResult(controller.DispatcherQueue);
                // The controller must stay alive for the process: the tests
                // enqueue to this queue, and with it dies every XAML object
                // they created.
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
            }
        });
        thread.IsBackground = true;
        thread.Start();

        try
        {
            if (!ready.Task.Wait(TurnCap))
            {
                throw new TimeoutException(
                    "the dedicated XAML thread never signalled its DispatcherQueue within the cap");
            }

            Queue = ready.Task.Result;

            // Dispatcher liveness, BEFORE any XAML: the two failure shapes
            // "the queue never dispatches at all" and "XAML object creation
            // hangs once it does" have different diagnoses, and the dry run
            // cannot tell them apart. A no-op turn answers it.
            var live = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Queue.TryEnqueue(() => live.TrySetResult(true)))
            {
                if (!live.Task.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        "the DispatcherQueue never dispatched a no-op turn; its dedicated thread is not pumping");
                }
            }
            else
            {
                throw new InvalidOperationException("the DispatcherQueue refused a no-op turn");
            }
        }
        catch (Exception ex)
        {
            // Never let the static constructor throw: a host that cannot
            // bootstrap must produce SKIPS, not a TypeInitializationException
            // that turns every gated test into an error.
            BootstrapFailure = ex;
        }
    }

    /// <summary>
    /// Run one turn of <paramref name="work"/> on the XAML thread, under
    /// <paramref name="cap"/>. The tests do ALL their platform work through
    /// this, so a wedged runtime surfaces as a timeout with a named
    /// host-fault message rather than a hung suite.
    /// </summary>
    internal static async Task<T> OnDispatcherAsync<T>(Func<T> work, TimeSpan? cap = null)
    {
        var limit = cap ?? TurnCap;
        var queue = Queue ?? throw new InvalidOperationException(
            "no XAML dispatcher: the bootstrap failed (" + BootstrapFailure?.Message + ")");
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
        {
            throw new InvalidOperationException("the XAML dispatcher refused a turn");
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(limit)).ConfigureAwait(false);
        if (done != tcs.Task)
        {
            throw new TimeoutException(
                "the XAML dispatcher did not answer within the cap; a wedged runtime is a host fault, not a test verdict");
        }

        return await tcs.Task.ConfigureAwait(false);
    }
}
