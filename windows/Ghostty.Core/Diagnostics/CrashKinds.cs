using System;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Diagnostics;

/// <summary>
/// Which layer of the stack a crash kind actually faults in.
///
/// The point of the coverage matrix is that different layers are seen by
/// different handlers, so the layer is the thing worth writing down: two
/// kinds in the same layer prove the same row twice.
/// </summary>
internal enum CrashLayer
{
    /// <summary>
    /// The managed shell, in-process, on whichever thread invoked the
    /// trigger. Covers the NativeAOT runtime's own failure paths.
    /// </summary>
    Managed,

    /// <summary>
    /// Zig, inside libghostty. Reached through a libghostty binding
    /// action, so the fault is raised by libghostty's own code rather than
    /// by a P/Invoke shim on the managed side.
    /// </summary>
    LibGhostty,

    /// <summary>
    /// The render thread, whichever backend is live. Deliberately not
    /// named after a backend: the panic sits in the shared renderer thread
    /// (<c>src/renderer/Thread.zig</c>), above the DirectX12 / DirectX11 /
    /// Metal / OpenGL split, so the same kind exercises whatever backend
    /// the build selected.
    /// </summary>
    Renderer,
}

/// <summary>
/// One deliberate crash trigger: what it is called, what it says on the
/// tin, and how it is reached.
/// </summary>
internal sealed record CrashKind
{
    /// <summary>
    /// The CLI spelling (<c>wintty +crash &lt;id&gt;</c>) and the stable
    /// half of the palette command id. Kebab-case, matching the CLI's
    /// other multi-word spellings.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The palette row's title. Carries the "Debug:" prefix and the word
    /// "crash" so a destructive developer action cannot be mistaken for an
    /// ordinary command in a list that is sorted by frecency.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>One line, shown under the title and by the CLI listing.</summary>
    public required string Description { get; init; }

    public required CrashLayer Layer { get; init; }

    /// <summary>
    /// The libghostty binding action this kind dispatches, or null when it
    /// is raised in-process by the managed trigger itself.
    ///
    /// Non-null is also what makes a kind need a live surface: a binding
    /// action has nowhere to go before a window exists, which is why the
    /// CLI cannot run these and the palette can.
    /// </summary>
    public string? BindingAction { get; init; }

    /// <summary>
    /// False for the one kind that is deliberately NOT a crash. It is in
    /// the set because "nothing is captured" is a result the matrix needs,
    /// and a probe that quietly took the process down instead would report
    /// the opposite of what it measured.
    /// </summary>
    public bool Crashes { get; init; } = true;

    /// <summary>
    /// Whether this kind can only run inside a window with a live surface.
    /// </summary>
    public bool NeedsSurface => BindingAction is not null;
}

/// <summary>
/// Every crash kind, once.
///
/// The CLI (<c>+crash</c>) and the command palette are two front doors on
/// one mechanism, and the failure this exists to prevent is the two
/// drifting: a kind added for the palette that <c>+crash</c> does not
/// know, or a CLI-only kind a developer cannot reach from inside a running
/// window. Both read this list; neither keeps its own.
///
/// Metadata only. Nothing here crashes anything, and nothing here is
/// wrapped in <c>#if DEBUG</c>: the mechanisms are (see
/// <c>Ghostty.Cli.CrashTrigger</c>) and so is the palette source that
/// offers them, so a Release build carries these strings with nothing able
/// to invoke them. Keeping the catalogue unconditional is what lets the
/// parity tests exercise the real list in any configuration, rather than
/// asserting against an empty one and passing.
/// </summary>
internal static class CrashKinds
{
    internal static readonly IReadOnlyList<CrashKind> All =
    [
        new()
        {
            Id = "native-seh",
            Title = "Debug: Crash (native SEH exception)",
            Description =
                "Raise a native SEH exception, which Windows dispatches through "
                + "the unhandled exception filter",
            Layer = CrashLayer.Managed,
        },
        new()
        {
            Id = "managed-unhandled",
            Title = "Debug: Crash (unhandled managed exception)",
            Description =
                "Throw an unhandled managed exception, which NativeAOT turns into "
                + "a fail-fast",
            Layer = CrashLayer.Managed,
        },
        new()
        {
            Id = "env-failfast",
            Title = "Debug: Crash (Environment.FailFast)",
            Description = "Fail fast, bypassing every user-mode handler by design",
            Layer = CrashLayer.Managed,
        },
        new()
        {
            Id = "stack-overflow",
            Title = "Debug: Crash (stack overflow)",
            Description =
                "Recurse until the thread's stack is exhausted, leaving no stack "
                + "for a handler to run on",
            Layer = CrashLayer.Managed,
        },
        new()
        {
            Id = "handled-storm",
            Title = "Debug: Handled exception storm (does not crash)",
            Description =
                "Throw and catch a thousand exceptions. Expected result: no crash "
                + "report at all",
            Layer = CrashLayer.Managed,
            Crashes = false,
        },
        new()
        {
            Id = "libghostty-main",
            Title = "Debug: Crash libghostty (main thread)",
            Description =
                "Panic inside libghostty on the thread that owns the surface, via "
                + "the crash binding action",
            Layer = CrashLayer.LibGhostty,
            BindingAction = "crash:main",
        },
        new()
        {
            Id = "libghostty-io",
            Title = "Debug: Crash libghostty (IO thread)",
            Description =
                "Panic on the terminal IO thread, which owns the pty and the "
                + "terminal state",
            Layer = CrashLayer.LibGhostty,
            BindingAction = "crash:io",
        },
        new()
        {
            Id = "renderer-thread",
            Title = "Debug: Crash the renderer (active backend)",
            Description =
                "Panic on the render thread, whichever GPU backend this build "
                + "selected",
            Layer = CrashLayer.Renderer,
            BindingAction = "crash:render",
        },
    ];

    /// <summary>
    /// The kind with this id, or null. Ordinal: these are wire spellings
    /// off a command line, not display text, and a culture-sensitive
    /// compare is how "native-seh" starts matching something else under a
    /// Turkish locale.
    /// </summary>
    internal static CrashKind? Find(string id) =>
        All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));

    /// <summary>Every id, comma-separated, for a CLI usage line.</summary>
    internal static string Ids => string.Join(", ", All.Select(k => k.Id));
}
