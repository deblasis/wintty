using System;
using System.Threading;
using Ghostty.Core;
using Xunit;

namespace Ghostty.Tests;

/// <summary>
/// The isolation half of the seam serialization pin (#854 item 9 review):
/// while a test in the <c>AppStateBaseSeamSerial</c> collection holds a
/// shadowed <see cref="AppStateBase.ReadEnvironment"/>, this collection,
/// which only reads state roots the way every ordinary parallel
/// collection does, must keep seeing the real environment.
///
/// <c>AppStateBase.ReadEnvironment</c> is a process-wide static seam, so
/// a swapped reader is visible to every concurrently running collection
/// for the duration of the test that swapped it; the serialized
/// collection is what keeps the shadow from leaking. This test samples
/// the override for a window and fails on the first shadowed read,
/// which is exactly the leak that serialization exists to prevent. Its
/// writer partner is
/// <see cref="AppStateBaseTests.TheShadowIsHeld_ForTheParallelIsolationReader"/>,
/// which holds a shadow for longer than this samples.
/// </summary>
public class AppStateBaseSeamIsolationTests
{
    [Fact]
    public void WhileTheShadowIsHeld_AParallelReaderKeepsTheRealRoot()
    {
        var deadline = Environment.TickCount64 + 1500;
        do
        {
            var shadow = AppStateBase.OverrideRoot;
            Assert.True(shadow is null,
                "a parallel, read-only collection saw the seam shadow " +
                $"'{shadow}': AppStateBase.ReadEnvironment was swapped by a " +
                "concurrent test. Every test that swaps the seam must sit in " +
                "the AppStateBaseSeamSerial collection.");
            Thread.Sleep(10);
        }
        while (Environment.TickCount64 < deadline);
    }
}
