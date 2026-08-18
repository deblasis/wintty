using System;
using System.Collections.Generic;

namespace Ghostty.Core.Activation;

/// <summary>
/// What a toast click asked the app to do. Today the only payload is the
/// surface the toast was raised for, so clicking it can put the user back on
/// the pane that spoke rather than on whatever window happens to be frontmost.
///
/// A struct with a null <see cref="SurfaceKey"/> (<see cref="None"/>) is the
/// "activated, but we cannot tell which surface" case -- an older toast that
/// predates the argument, a toast from a previous process, or a malformed
/// payload. Callers must treat it as an ordinary activation instead of
/// failing: a click that does nothing is worse than a click that just brings
/// the app forward.
/// </summary>
internal readonly record struct ToastActivation(string? SurfaceKey)
{
    /// <summary>
    /// Key under which the surface travels in the toast's own argument bag
    /// (<c>AppNotificationBuilder.AddArgument</c> writing it,
    /// <c>AppNotificationActivatedEventArgs.Arguments</c> reading it back).
    /// </summary>
    public const string SurfaceArgumentKey = "surface";

    // The same fact spelled as a command-line argument, for the hop from a
    // secondary process to the single-instance primary. It rides inside argv
    // rather than as a new field in the LaunchRequest wire format because the
    // primary is the OLDER build during an upgrade: it is the process that was
    // already running. A new wire field would make that primary reject the
    // whole payload and drop the launch, whereas an argument it does not know
    // is simply ignored and the launch degrades to opening a window.
    private const string ForwardedFlagPrefix = "--toast-surface=";

    /// <summary>An activation carrying no surface. See the type remarks.</summary>
    public static ToastActivation None => default;

    public bool HasSurface => !string.IsNullOrEmpty(SurfaceKey);

    /// <summary>
    /// Read the surface out of a toast's argument bag. An absent or empty
    /// value degrades to <see cref="None"/>; unknown keys are ignored so a
    /// toast raised by a newer build still activates this one.
    /// </summary>
    public static ToastActivation FromNotificationArguments(
        IDictionary<string, string>? arguments)
    {
        if (arguments is null) return None;
        if (!arguments.TryGetValue(SurfaceArgumentKey, out var key)) return None;
        return string.IsNullOrEmpty(key) ? None : new ToastActivation(key);
    }

    /// <summary>
    /// Spell this activation as one extra argv entry for the forward to the
    /// single-instance primary.
    /// </summary>
    public static string ForwardedArg(string surfaceKey)
        => ForwardedFlagPrefix + surfaceKey;

    /// <summary>
    /// Recover an activation a secondary process forwarded through argv.
    /// Last one wins, matching how <c>JumpListLaunch.Parse</c> reads the same
    /// vector, so the entry the forwarder appended beats anything the user
    /// happened to type. An empty value degrades to <see cref="None"/>.
    /// </summary>
    public static ToastActivation FromForwardedArgs(IReadOnlyList<string>? args)
    {
        if (args is null) return None;

        var result = None;
        foreach (var arg in args)
        {
            if (!arg.StartsWith(ForwardedFlagPrefix, StringComparison.Ordinal)) continue;
            var key = arg[ForwardedFlagPrefix.Length..];
            result = key.Length == 0 ? None : new ToastActivation(key);
        }

        return result;
    }
}
