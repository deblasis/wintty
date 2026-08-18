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
    /// The argv a secondary forwards to the primary: the caller's own command
    /// line, with a TRAILING marker dropped, then this activation appended
    /// when there is one.
    ///
    /// The final position is reserved for the forwarder, and that is the only
    /// position <see cref="FromForwardedArgs"/> reads. Dropping a trailing one
    /// is what stops a user's own command line fabricating a click -- typing
    /// <c>wintty -e sometool --toast-surface=x</c> must not make the primary
    /// focus a surface and swallow the launch.
    ///
    /// An occurrence anywhere EARLIER is left alone. It is the user's
    /// argument, it cannot be mistaken for a forwarded one, and the primary
    /// has to receive the command line that was actually typed.
    /// </summary>
    public static List<string> ForwardedArgv(
        IReadOnlyList<string> args, ToastActivation activation)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new List<string>(args.Count + 1);
        result.AddRange(args);

        if (result.Count > 0 && IsForwardedFlag(result[result.Count - 1]))
            result.RemoveAt(result.Count - 1);

        if (activation.SurfaceKey is { Length: > 0 } key)
            result.Add(ForwardedFlagPrefix + key);

        return result;
    }

    /// <summary>
    /// Recover an activation a secondary forwarded through argv.
    ///
    /// Only the FINAL element counts, because that is the one position the
    /// forwarder controls: it appends there after stripping. An occurrence
    /// anywhere else is something the user typed, and honouring it would let a
    /// command line fabricate a click. An empty value degrades to
    /// <see cref="None"/>.
    /// </summary>
    public static ToastActivation FromForwardedArgs(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0) return None;

        var last = args[args.Count - 1];
        if (!IsForwardedFlag(last)) return None;

        var key = last[ForwardedFlagPrefix.Length..];
        return key.Length == 0 ? None : new ToastActivation(key);
    }

    private static bool IsForwardedFlag(string arg)
        => arg.StartsWith(ForwardedFlagPrefix, StringComparison.Ordinal);
}
