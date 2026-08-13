using System;
using Ghostty.Core.Notifications;

namespace Ghostty.Core.Renderer;

/// <summary>
/// Turns a renderer <see cref="CustomShaderFailure"/> into the one-time
/// <see cref="Notice"/> explaining why the user's <c>custom-shader</c> is not
/// being applied.
/// </summary>
/// <remarks>
/// <para>
/// The failure is otherwise invisible. The renderer falls back to drawing
/// straight to the target, so the terminal looks completely normal and the
/// only trace is a Zig <c>log.warn</c> in a file nobody opens.
/// </para>
/// <para>
/// Stateful because the signal repeats: libghostty raises the action once per
/// surface per shader initialization, so one config reload with three panes
/// open produces three actions. <see cref="Notice.DedupKey"/> collapses those
/// into a single banner while one is on screen; this gate is what stops the
/// banner returning after the user dismissed it and later reloaded config for
/// an unrelated reason.
/// </para>
/// <para>
/// The gate keys on the reason alone, deliberately. Re-arming when the reason
/// changes (a syntax error becoming a pipeline failure) is new information
/// worth showing; the same reason again is not, whatever else changed. Keying
/// on the configured shader path as well was considered and dropped — the only
/// getter for it returns the first entry of a repeatable option, so it would
/// have been wrong for anyone with two shaders configured.
/// </para>
/// <para>
/// UI-free and dependency-free so the gating and the copy unit-test without a
/// WinUI runtime, mirroring <c>NoColorStartup</c>. Not thread-safe: call it on
/// the UI thread like everything else touching
/// <see cref="INotificationService"/>.
/// </para>
/// </remarks>
public sealed class CustomShaderNoticeSource
{
    /// <summary>
    /// One key for every variant, so several surfaces failing at once show a
    /// single banner rather than one per pane.
    /// </summary>
    public const string DedupKey = "custom-shader";

    // The reason behind the last notice we returned; null until the first one.
    private CustomShaderFailure? _lastNotified;

    /// <param name="failure">The reason reported by the renderer.</param>
    /// <returns>
    /// The notice to show, or null when it would just repeat the last one.
    /// </returns>
    public Notice? Resolve(CustomShaderFailure failure)
    {
        if (_lastNotified == failure) return null;
        _lastNotified = failure;

        return new Notice
        {
            Title = "Custom shader not applied",
            Message = MessageFor(failure),
            // Warning, not Informational: something the user explicitly
            // configured is doing nothing. Not Error -- nothing is broken,
            // the terminal renders normally without the shader.
            Severity = NoticeSeverity.Warning,
            DedupKey = DedupKey,
            // No actions, deliberately. We cannot retry a compile the driver
            // already rejected, we will not silently rewrite the user's
            // config, and an "Open settings" button would land on a page that
            // cannot show what the compiler objected to. NO_COLOR earns its
            // buttons because there is a real reversible choice behind them;
            // here there is only information.
        };
    }

    private static string MessageFor(CustomShaderFailure failure) => failure switch
    {
        CustomShaderFailure.LoadFailed =>
            "Wintty could not read or translate the shader file in your custom-shader "
            + "setting, so it is being skipped. Check the path; the terminal renders "
            + "normally without it.",
        CustomShaderFailure.CompilerUnavailable =>
            "Wintty could not load the DirectX shader compiler (dxcompiler.dll), so "
            + "custom-shader has no effect in this build. The terminal renders normally "
            + "without it.",
        CustomShaderFailure.CompileFailed =>
            "Your custom-shader did not compile, so it is being skipped. The compiler's "
            + "errors are in the Wintty log; the terminal renders normally without it.",
        // PipelineFailed, and any future variant an older build does not know:
        // the shader was readable and compilable but the GPU would not take it.
        _ =>
            "Your custom-shader compiled but the graphics pipeline for it could not be "
            + "created, so it is being skipped. The terminal renders normally without it.",
    };
}
