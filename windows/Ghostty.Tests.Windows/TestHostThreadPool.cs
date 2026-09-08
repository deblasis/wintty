using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Gives this test host a thread pool big enough for the way xunit runs it,
/// before any test starts.
/// </summary>
/// <remarks>
/// <para>
/// The pool starts with <c>ProcessorCount</c> worker threads and, past that,
/// injects roughly one or two a second. xunit runs test collections in
/// parallel up to <c>ProcessorCount</c>, and several tests here hold a pool
/// thread while they wait on something else: the spawn probe blocks on a
/// child process, the pipe tests block on a handshake. When those fill the
/// pool, anything that needs a FRESH work item does not run until the pool
/// grows -- and that includes <see cref="Timer"/> callbacks.
/// </para>
/// <para>
/// That is not a hypothesis. The active-process tracker polls on a Timer, and
/// with the pool deliberately saturated it was measured raising 0 events in
/// 8 seconds, and 1 in a second pass, against roughly one a second unloaded.
/// A whole-assembly run reproduced it: the wsl smoke test failed reporting
/// "observed: []" -- not a wrong value, no values at all -- while the walker
/// could see the process it was waiting for the whole time.
/// </para>
/// <para>
/// Four times <c>ProcessorCount</c> leaves every parallel test free to block
/// a thread and still keeps threads spare for timers and continuations, so
/// what the tracker tests measure is the tracker rather than the runner's
/// thread supply. This changes the test host only; nothing here is compiled
/// into the product.
/// </para>
/// </remarks>
internal static class TestHostThreadPool
{
    [ModuleInitializer]
    internal static void RaiseMinimumWorkers()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        var wanted = Environment.ProcessorCount * 4;
        ThreadPool.SetMinThreads(
            Math.Max(workers, wanted),
            Math.Max(completionPorts, wanted));
    }
}
