using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Interop;

// The program status protocol (OSC 7501): a program reports what it is
// doing (idle, working, done, blocked on the user, failed) and the
// terminal keeps what it said. The core parses and validates each report
// and hands it over as one action; what the app does with the records is
// the app's. Everything in a report is untrusted input from the program.

/// <summary>
/// What a program status action is. Explicitly numbered: the values are
/// the core's <c>ghostty_action_program_status_event_e</c> ordinals.
/// </summary>
public enum ProgramStatusEventKind
{
    /// <summary>A validated OSC 7501 report (including clear).</summary>
    Report = 0,
    /// <summary>A full reset (RIS): every record is gone.</summary>
    Reset = 1,
    /// <summary>A new shell prompt (OSC 133 A).</summary>
    PromptStart = 2,
}

/// <summary>
/// A report's state. Explicitly numbered: the values are the core's
/// <c>ghostty_action_program_status_state_e</c> ordinals.
/// </summary>
public enum ProgramStatusState
{
    Idle = 0,
    Working = 1,
    Done = 2,
    Blocked = 3,
    Error = 4,
    /// <summary>Not a state: removes the addressed record and its subtree.</summary>
    Clear = 5,
}

/// <summary>
/// What a blocked program needs. Explicitly numbered: the values are the
/// core's <c>ghostty_action_program_status_kind_e</c> ordinals.
/// </summary>
public enum ProgramStatusKind
{
    None = 0,
    Permission = 1,
    Question = 2,
    Auth = 3,
}

/// <summary>
/// One report as the core delivered it. Absent text is empty, an absent
/// progress is -1. A report is one OSC 7501 line about one id; the pane
/// it arrived on is the surface the action was addressed to.
/// </summary>
public readonly record struct ProgramStatusReport(
    ProgramStatusState State,
    ProgramStatusKind Kind,
    int Progress,
    string Id,
    string App,
    string Title,
    string Message);

/// <summary>One program status action, copied out of the core's callback
/// memory: what happened, the report when one was carried, and the
/// surface's desktop-notifications setting at the time.</summary>
public readonly record struct ProgramStatusEvent(
    ProgramStatusEventKind Kind,
    ProgramStatusReport Report,
    bool DesktopNotifications = true);

/// <summary>
/// The specification's size limits, as the core's parser enforces them.
/// The decoder refuses anything longer on arrival rather than trust a
/// length it was handed.
/// </summary>
public static class ProgramStatusLimits
{
    public const int MaxIdBytes = 128;
    public const int MaxAppBytes = 32;
    public const int MaxTitleBytes = 192;
    public const int MaxMessageBytes = 2048;
}

/// <summary>
/// Copies a program status action out of the core's callback memory. Must
/// run inside the callback: the report and its strings are borrowed.
/// </summary>
/// <remarks>
/// The core validated the report already; this refuses anything that could
/// not have come from it (an unknown event, state or kind, a length past
/// the specification's limit, a null pointer with a length) rather than
/// read past a buffer on the strength of a length it was handed.
/// </remarks>
internal static class ProgramStatusActionDecoder
{
    internal static unsafe bool TryDecode(nint payload, out ProgramStatusEvent evt)
    {
        evt = default;
        if (payload == 0) return false;
        var action = Unsafe.ReadUnaligned<GhosttyActionProgramStatus>((void*)payload);
        switch (action.Event)
        {
            case (int)ProgramStatusEventKind.Reset:
                evt = new ProgramStatusEvent(ProgramStatusEventKind.Reset, default, action.DesktopNotifications != 0);
                return true;
            case (int)ProgramStatusEventKind.PromptStart:
                evt = new ProgramStatusEvent(ProgramStatusEventKind.PromptStart, default, action.DesktopNotifications != 0);
                return true;
            case (int)ProgramStatusEventKind.Report:
                break;
            default:
                return false;
        }

        if (action.Report == 0) return false;
        var r = Unsafe.ReadUnaligned<GhosttyActionProgramStatusReport>((void*)action.Report);
        if (r.State is < 0 or > (int)ProgramStatusState.Clear) return false;
        if (r.Kind is < 0 or > (int)ProgramStatusKind.Auth) return false;

        if (!TryString(r.Id, r.IdLen, ProgramStatusLimits.MaxIdBytes, out var id) ||
            !TryString(r.App, r.AppLen, ProgramStatusLimits.MaxAppBytes, out var app) ||
            !TryString(r.Title, r.TitleLen, ProgramStatusLimits.MaxTitleBytes, out var title) ||
            !TryString(r.Message, r.MessageLen, ProgramStatusLimits.MaxMessageBytes, out var message))
        {
            return false;
        }

        evt = new ProgramStatusEvent(ProgramStatusEventKind.Report, new ProgramStatusReport(
            (ProgramStatusState)r.State,
            (ProgramStatusKind)r.Kind,
            r.Progress,
            id, app, title, message), action.DesktopNotifications != 0);
        return true;
    }

    private static bool TryString(nint ptr, nuint len, int max, out string value)
    {
        value = string.Empty;
        if (len == 0) return true;
        if (ptr == 0 || len > (nuint)max) return false;
        value = Marshal.PtrToStringUTF8(ptr, (int)len);
        return true;
    }
}
