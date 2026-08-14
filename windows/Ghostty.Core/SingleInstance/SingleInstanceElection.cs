using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// What a launch turned out to be, once the election has been held.
/// </summary>
public enum SingleInstanceRole
{
    /// <summary><c>windows-single-instance</c> is off; nothing was created.</summary>
    Disabled,

    /// <summary>This process owns the session and serves forwarded launches.</summary>
    Primary,

    /// <summary>A primary already exists; this launch is forwarded to it.</summary>
    Secondary,

    /// <summary>The election could not be held. See <see cref="SingleInstanceElection.Failure"/>.</summary>
    Failed,
}

/// <summary>
/// The single-instance decision, made once per process.
/// </summary>
/// <remarks>
/// Creating the mutex IS the decision: <c>CreateMutex</c> is atomic, so exactly
/// one of any number of racing processes sees <c>createdNew</c>. Asking whether
/// the mutex exists and then acting on the answer decides twice, and the answer
/// can change in between.
///
/// Held before the app runtime exists, so there is no logger to report to; a
/// failure is carried in <see cref="Failure"/> for the caller to log later. A
/// failed election is not a blocked launch - the process continues as an
/// ordinary independent window.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceElection : IDisposable
{
    private SingleInstanceElection(
        SingleInstanceRole role,
        Mutex? mutex,
        SingleInstanceNames.Names names,
        Exception? failure)
    {
        Role = role;
        Mutex = mutex;
        Names = names;
        Failure = failure;
    }

    /// <summary>What this launch turned out to be.</summary>
    public SingleInstanceRole Role { get; }

    /// <summary>
    /// The mutex, on a <see cref="SingleInstanceRole.Primary"/> only. Must stay
    /// reachable for the process lifetime: a collected handle releases the name
    /// and lets a later launch elect itself primary alongside this one.
    /// </summary>
    public Mutex? Mutex { get; }

    /// <summary>The mutex and pipe names this election was held under.</summary>
    public SingleInstanceNames.Names Names { get; }

    /// <summary>Why the election could not be held, on a
    /// <see cref="SingleInstanceRole.Failed"/> only.</summary>
    public Exception? Failure { get; }

    /// <summary>
    /// Whether this launch should put the splash up.
    /// </summary>
    /// <remarks>
    /// A secondary must not, even for the moment before it forwards: the splash
    /// is full-size, opaque and topmost, and the window it would cover is the
    /// primary's. Every other role goes on to open a window of its own.
    /// </remarks>
    public bool ShouldShowLaunchSplash => Role != SingleInstanceRole.Secondary;

    /// <summary>
    /// Hold the election. Creates nothing when <paramref name="enabled"/> is
    /// false, so a process with the feature off never leaves a mutex behind for
    /// one with it on to misread.
    /// </summary>
    public static SingleInstanceElection Run(bool enabled, string exePath)
    {
        var names = SingleInstanceNames.For(exePath);

        if (!enabled)
            return new SingleInstanceElection(SingleInstanceRole.Disabled, null, names, null);

        try
        {
            var mutex = new Mutex(initiallyOwned: true, name: names.Mutex, out var createdNew);
            if (createdNew)
                return new SingleInstanceElection(SingleInstanceRole.Primary, mutex, names, null);

            // Close the loser's handle here rather than carrying it. A named
            // mutex outlives its owner for as long as ANY handle is open, so
            // holding this one across startup would keep the name alive after
            // the primary exits - and the next launch would elect itself
            // secondary with nobody left to forward to.
            mutex.Dispose();
            return new SingleInstanceElection(SingleInstanceRole.Secondary, null, names, null);
        }
        catch (Exception ex)
        {
            return new SingleInstanceElection(SingleInstanceRole.Failed, null, names, ex);
        }
    }

    /// <summary>
    /// Release the mutex, so a relaunch can become the new primary immediately.
    /// A no-op on any role but <see cref="SingleInstanceRole.Primary"/>.
    /// </summary>
    public void Dispose() => Mutex?.Dispose();
}
