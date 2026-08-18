using System;
using System.Collections.Generic;

namespace Ghostty.Core.Activation;

/// <summary>
/// The command-line half of protocol activation. A packaged launch gets its
/// URI from <c>AppInstance.GetActivatedEventArgs</c>; an unpackaged one is a
/// plain process start, so the shell registration passes the URI as
/// <c>--uri &lt;url&gt;</c> and this recovers it.
///
/// Pure and argv-only on purpose: the WinRT probe is the fragile half (it
/// throws on unpackaged builds), so the fallback it is paired with must not
/// share its failure. Keeping the scan here means the caller can run it after
/// a throw as easily as after a miss.
/// </summary>
internal static class ProtocolLaunch
{
    private const string UriFlag = "--uri";

    /// <summary>
    /// The URI this launch is about. <paramref name="protocolUri"/> is what
    /// the WinRT probe produced, or null both when it found no protocol
    /// activation AND when it threw -- the two cases the argv scan has to
    /// cover, and the reason the caller must not nest the scan inside the
    /// probe's try block.
    ///
    /// A real protocol activation always wins: argv is a fallback for a
    /// launch the packaged path could not describe, never an override of one
    /// it could.
    /// </summary>
    public static Uri? Resolve(Uri? protocolUri, IReadOnlyList<string>? args)
        => protocolUri ?? ParseUri(args);

    /// <summary>
    /// First absolute URI following a <c>--uri</c> argument, or null when
    /// argv carries none. A trailing <c>--uri</c> with nothing after it, or a
    /// value that is not an absolute URI, yields null rather than throwing:
    /// argv is untrusted input and a bad one must not cost the user a launch.
    /// A <c>--uri</c> whose value does not parse is skipped rather than
    /// terminating the scan, so a later well-formed pair still wins.
    /// </summary>
    public static Uri? ParseUri(IReadOnlyList<string>? args)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], UriFlag, StringComparison.Ordinal)
                && Uri.TryCreate(args[i + 1], UriKind.Absolute, out var uri))
                return uri;
        }

        return null;
    }
}
