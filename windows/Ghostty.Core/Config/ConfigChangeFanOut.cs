using System;

namespace Ghostty.Core.Config;

/// <summary>
/// Delivers a config-change notification to every subscriber, containing a
/// fault in one so it cannot cost the others their notification.
///
/// Two separate hazards make this worth a helper rather than a try/catch at
/// the call site.
///
/// The first is that the fan-out runs inside a DispatcherQueueHandler. An
/// exception that escapes one cannot be marshalled back to whoever enqueued
/// it, so WinRT stows it and fail-fasts the process with
/// STATUS_STOWED_EXCEPTION. The .NET crash hooks never see it, so there is
/// no managed stack in any dump and no crash report -- the process simply
/// dies with an unattributable native code.
///
/// The second is that a plain multicast invoke stops at the first subscriber
/// that throws, silently skipping every subscriber behind it. Since each
/// window subscribes for its own chrome, one window failing to apply a
/// reload would otherwise leave every other window painting against a config
/// the app has already moved past. Walking the invocation list and
/// containing each subscriber separately is what keeps the rest in sync.
/// </summary>
public static class ConfigChangeFanOut
{
    /// <summary>
    /// Invoke every subscriber with <paramref name="arg"/>. Faults are
    /// reported to <paramref name="onFault"/> and never propagate.
    /// </summary>
    public static void InvokeAll<T>(Action<T>? handlers, T arg, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(onFault);
        if (handlers is null) return;

        // GetInvocationList rather than DynamicInvoke: the cast is static, so
        // this stays trimming- and AOT-safe. The array allocation is paid
        // once per config reload, which is a user-driven event.
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try { ((Action<T>)subscriber)(arg); }
            catch (Exception ex) { Report(onFault, ex); }
        }
    }

    /// <summary>
    /// Invoke every subscriber. Faults are reported to
    /// <paramref name="onFault"/> and never propagate.
    /// </summary>
    public static void InvokeAll(Action? handlers, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(onFault);
        if (handlers is null) return;

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try { ((Action)subscriber)(); }
            catch (Exception ex) { Report(onFault, ex); }
        }
    }

    /// <summary>
    /// A logging callback that throws must not resurrect the crash this
    /// type exists to prevent, so reporting is contained too.
    /// </summary>
    private static void Report(Action<Exception> onFault, Exception ex)
    {
        try { onFault(ex); }
        catch { }
    }
}
