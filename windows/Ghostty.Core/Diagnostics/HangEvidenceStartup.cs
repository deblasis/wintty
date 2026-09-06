using System;
using System.Globalization;

namespace Ghostty.Core.Diagnostics;

/// <summary>
/// Outcome of looking for hang evidence the user has not been told
/// about: whether a notice is warranted, and when the newest stall was
/// logged.
/// </summary>
public readonly record struct HangEvidenceOutcome(bool Notify, DateTimeOffset NewestStall);

/// <summary>
/// Decides whether a previous session left an unreported UI-thread
/// stall in crash.log (#1046). The watchdog records the stall while the
/// UI thread is hung, and a hung UI thread cannot show anything, so the
/// next launch is the first moment the user can be told. This resolver
/// is that decision and nothing else: pure and I/O-free so it
/// unit-tests without a filesystem, and allocation-light because it
/// runs on the launch path. The app layer supplies the tail of
/// crash.log and the persisted last-launch instant, and owns every
/// byte of the I/O.
/// </summary>
public static class HangEvidenceStartup
{
    /// <summary>
    /// The marker the hang watchdog writes into crash.log, held here so
    /// the writer and the reader share one spelling.
    /// </summary>
    public const string StallMarker = "[UI-THREAD STALL]";

    /// <summary>
    /// Newest stall entry in <paramref name="crashLogTail"/> that is
    /// newer than <paramref name="lastLaunch"/>, or
    /// <c>Notify = false</c> when there is none. Entries are appended
    /// chronologically, so the scan walks from the end and the first
    /// line that parses is the newest; a partial trailing line (a crash
    /// mid-write can leave one) is skipped rather than fatal.
    /// </summary>
    public static HangEvidenceOutcome Resolve(string? crashLogTail, DateTimeOffset lastLaunch)
    {
        if (string.IsNullOrEmpty(crashLogTail)) return default;

        var rest = crashLogTail.AsSpan();
        while (!rest.IsEmpty)
        {
            var lastNewline = rest.LastIndexOf('\n');
            // After the last newline is the final line; before it is
            // everything older.
            var line = lastNewline < 0 ? rest : rest[(lastNewline + 1)..];
            rest = lastNewline < 0 ? default : rest[..lastNewline];

            if (TryReadStallTimestamp(line, out var stall) && stall > lastLaunch)
                return new HangEvidenceOutcome(Notify: true, NewestStall: stall);
        }

        return default;
    }

    /// <summary>
    /// A stall entry starts with its O-format timestamp (which never
    /// contains a space) and carries the marker somewhere in the line.
    /// The unhandled-exception tags match the timestamp shape but not
    /// the marker; the free-text detail lines match neither.
    /// </summary>
    private static bool TryReadStallTimestamp(ReadOnlySpan<char> line, out DateTimeOffset stall)
    {
        stall = default;
        var space = line.IndexOf(' ');
        if (space <= 0 || !line.Contains(StallMarker, StringComparison.Ordinal)) return false;
        return DateTimeOffset.TryParse(
            line[..space],
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out stall);
    }
}
