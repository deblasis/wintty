using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using WinRT;
using Xunit;
using DynamicDependency = Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace Ghostty.Tests.Windows;

/// <summary>
/// The platform premise the hidden-tab creation fix bets on, asserted
/// against the real runtime: <c>FrameworkElement.LayoutUpdated</c> IS
/// delivered to an element whose ancestor is <c>Collapsed</c>.
///
/// TerminalControl arms two lifetime handlers on
/// <c>Panel.LayoutUpdated</c>: the creation one-shot and the show-path
/// backstop (<c>OnLayoutShowWatch</c>). The backstop only works because
/// passes that arrive while the pane is hidden still DELIVER: they are
/// how the pane records that a hidden era consumed its drivers, and the
/// collapse-to-visible flip is how it observes the show. If the platform
/// ever delivered LayoutUpdated only to arranged elements, every guard
/// in <c>HiddenTabMeasureWiringTests</c> would stay green while hidden
/// panes starved of their surface again - which is why the premise is
/// pinned here, on the platform, and not trusted from the wiring parse.
///
/// The tension this resolves is the repo's own: NotificationHost
/// documents that a collapsed element is skipped by Measure and Arrange
/// and its SizeChanged never fires at all. LayoutUpdated is not
/// arrange-driven - it is the tree-wide end-of-layout notification - and
/// the difference between the two events is the whole margin of the
/// design. A detached tree cannot answer this (measured: a manually
/// measured-and-arranged detached root raises no LayoutUpdated at all),
/// so the test attaches its tree to a real window.
///
/// The window is never visible to anyone and never takes the foreground:
/// it is moved to coordinates on no monitor and shown with activation
/// suppressed, then closed in the same test. The runtime lives on one
/// dedicated dispatcher thread with a WindowsXamlManager, which a bare
/// test host needs before it may create XAML elements at all; that
/// manager is also why this test brings its own thread instead of the
/// probe the animation lifecycle tests use (their facet skips on hosts
/// where element creation hangs without one).
/// </summary>
public sealed class CollapsedAncestorLayoutDeliveryTests
{
    private static readonly TimeSpan PumpCap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The shared XAML thread, created once per test host. The manager
    /// must be initialized on a thread that already has a DispatcherQueue;
    /// <c>DispatcherQueueController.CreateOnDedicatedThread()</c> provides
    /// both, and everything the test does with XAML objects is enqueued
    /// onto that queue so their thread affinity is respected.
    /// </summary>
    private static DispatcherQueue? _queue;

    private static DispatcherQueue XamlThread()
    {
        if (_queue is { } have) return have;
        DynamicDependency.Bootstrap.TryInitialize(0x00020000, out _);
        _queue = DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;
        return _queue;
    }

    private static Task<T> OnXamlThreadAsync<T>(Func<T> work)
    {
        var queue = XamlThread();
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
        {
            throw new InvalidOperationException(
                "the XAML dispatcher refused a turn; a wedged runtime is a " +
                "host fault, not a test verdict");
        }

        return WaitFor(tcs);
    }

    private static async Task<T> WaitFor<T>(TaskCompletionSource<T> tcs)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(PumpCap))
            .ConfigureAwait(false);
        if (done != tcs.Task)
        {
            throw new TimeoutException(
                "the XAML dispatcher did not answer within the cap; a wedged " +
                "runtime is a host fault, not a test verdict");
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Read a counter on the XAML thread, polling until it reaches
    /// <paramref name="atLeast"/> or the cap expires. LayoutUpdated is
    /// raised by the tree's layout tick, not synchronously by the
    /// invalidation, so the pane is given the pump's own time to deliver.
    /// </summary>
    private static async Task<int> AwaitTicksAsync(Func<int> read, int atLeast)
    {
        var deadline = DateTime.UtcNow + PumpCap;
        while (DateTime.UtcNow < deadline)
        {
            var seen = await OnXamlThreadAsync(read).ConfigureAwait(false);
            if (seen >= atLeast) return seen;
            await Task.Delay(PollEvery).ConfigureAwait(false);
        }

        return await OnXamlThreadAsync(read).ConfigureAwait(false);
    }

    [Fact]
    public async Task LayoutUpdated_IsDelivered_UnderACollapsedAncestor()
    {
        int collapsed = 0;
        int visible = 0;
        Window? window = null;
        try
        {
            await OnXamlThreadAsync<object?>(() =>
            {
                var manager = WindowsXamlManager.InitializeForCurrentThread();
                _ = manager; // kept alive by the thread for the test's duration

                window = new Window();
                // Offscreen and never activated: Show(false) does not take
                // the foreground, -32000,-32000 is on no monitor, and the
                // switcher flag keeps it out of the taskbar for the test's
                // duration.
                window.AppWindow.MoveAndResize(
                    new global::Windows.Graphics.RectInt32(-32000, -32000, 800, 600));
                window.AppWindow.IsShownInSwitchers = false;
                window.AppWindow.Show(activateWindow: false);

                var root = new Grid();
                var hiddenHost = new Border { Visibility = Visibility.Collapsed };
                var hiddenLeaf = new Grid();
                hiddenHost.Child = hiddenLeaf;
                var shownHost = new Border();
                var shownLeaf = new Grid();
                shownHost.Child = shownLeaf;
                root.Children.Add(hiddenHost);
                root.Children.Add(shownHost);

                // The leaf under the collapsed ancestor is never measured
                // or arranged (the NotificationHost fact); the question is
                // whether its LayoutUpdated still fires. The leaf under the
                // visible one is the control that separates "collapsed
                // panes are skipped" from "this harness fired nothing".
                hiddenLeaf.LayoutUpdated += (_, _) => collapsed++;
                shownLeaf.LayoutUpdated += (_, _) => visible++;

                window.Content = root;
                return null;
            }).ConfigureAwait(false);

            var visibleTicks = await AwaitTicksAsync(() => visible, 1).ConfigureAwait(false);
            Assert.True(visibleTicks > 0,
                "the harness itself is blind: LayoutUpdated was not delivered " +
                "even to the leaf under a VISIBLE ancestor in an attached " +
                "window, so the collapsed leaf's count below would be vacuous");

            var collapsedTicks = await AwaitTicksAsync(() => collapsed, 1).ConfigureAwait(false);
            Assert.True(collapsedTicks > 0,
                "LayoutUpdated was NOT delivered to the leaf under a Collapsed " +
                "ancestor: the hidden-tab creation fix's show-path backstop " +
                "rides this event to learn about hidden eras and to observe " +
                "the show flip, and a platform that skips collapsed subtrees " +
                "would starve hidden panes of their surface again");

            // The flip: the pane's own show. The backstop settles pending
            // creation on exactly this pass, so it must be delivered too.
            await OnXamlThreadAsync<object?>(() =>
            {
                var root = (Grid)window!.Content!;
                var hiddenHost = (Border)root.Children[0];
                hiddenHost.Visibility = Visibility.Visible;
                root.UpdateLayout();
                return null;
            }).ConfigureAwait(false);

            var collapsedAfterFlip = await AwaitTicksAsync(() => collapsed, 2).ConfigureAwait(false);
            Assert.True(collapsedAfterFlip >= 2,
                "the collapse-to-visible flip did not deliver another " +
                "LayoutUpdated to the flipped leaf: the show-path backstop " +
                "settles pending creation on this pass, and a flip that " +
                "raises nothing would leave the starved pane starved");
        }
        finally
        {
            if (window is not null)
            {
                await OnXamlThreadAsync<object?>(() =>
                {
                    window.Close();
                    return null;
                }).ConfigureAwait(false);
            }
        }
    }
}
