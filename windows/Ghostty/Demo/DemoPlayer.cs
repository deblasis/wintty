#if DEMO
using System;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Demo;
using Ghostty.Core.Input;
using Microsoft.Extensions.Logging;

namespace Ghostty.Demo;

/// <summary>
/// Drives a <see cref="DemoScript"/> against the live app. Runs as an async
/// coroutine on the UI dispatcher thread: each beat is dispatched, then the
/// player awaits either a timed gap (auto) or a step signal (stepped). Esc
/// cancels via the token; cleanup always runs in finally.
///
/// Decoupled from XAML via callbacks: invokeAction, invokeBinding, and
/// injectText are the three ways it touches the terminal; the overlay
/// callbacks render captions.
/// </summary>
internal sealed class DemoPlayer
{
    private readonly Action<PaneAction> _invokeAction;
    private readonly Action<string> _invokeBinding;
    private readonly Action<string> _injectText;
    private readonly Action<string, string> _applyConfig; // config key, value
    private readonly Action<string, int?, int?> _showCaption; // text, stepIndex, stepTotal
    private readonly Action _hideOverlay;
    private readonly ILogger _log;

    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _stepGate;
    private volatile bool _paused;

    public bool IsRunning { get; private set; }

    public DemoPlayer(
        Action<PaneAction> invokeAction,
        Action<string> invokeBinding,
        Action<string> injectText,
        Action<string, string> applyConfig,
        Action<string, int?, int?> showCaption,
        Action hideOverlay,
        ILogger log)
    {
        _invokeAction = invokeAction;
        _invokeBinding = invokeBinding;
        _injectText = injectText;
        _applyConfig = applyConfig;
        _showCaption = showCaption;
        _hideOverlay = hideOverlay;
        _log = log;
    }

    /// <summary>Advance one beat in stepped mode (no-op otherwise).</summary>
    public void Step() => _stepGate?.TrySetResult(true);

    /// <summary>Toggle pause in auto mode.</summary>
    public void TogglePause() => _paused = !_paused;

    /// <summary>Abort a running demo.</summary>
    public void Abort() => _cts?.Cancel();

    public async Task RunAsync(DemoScript script, DemoMode mode)
    {
        if (IsRunning)
        {
            _log.LogInformation("Demo already running; ignoring start request.");
            return;
        }

        IsRunning = true;
        _paused = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            for (var i = 0; i < script.Beats.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var beat = script.Beats[i];

                await DispatchBeatAsync(script, beat, mode, i + 1, ct);

                if (mode == DemoMode.Stepped)
                    await WaitForStepAsync(ct);
                else
                {
                    var gap = beat.DurationMs ?? script.BeatGapMs;
                    await DelayWithPauseAsync(gap, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Demo aborted.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Demo failed.");
        }
        finally
        {
            _hideOverlay();
            _cts?.Dispose();
            _cts = null;
            _stepGate = null;
            IsRunning = false;
        }
    }

    private async Task DispatchBeatAsync(
        DemoScript script, DemoBeat beat, DemoMode mode, int stepNumber, CancellationToken ct)
    {
        switch (beat.Type)
        {
            case "caption":
                if (beat.Text is { } caption)
                {
                    // Show the "n / total" indicator only in stepped mode.
                    var stepped = mode == DemoMode.Stepped;
                    _showCaption(
                        caption,
                        stepped ? stepNumber : null,
                        stepped ? script.Beats.Count : null);
                }
                break;

            case "action":
                if (DemoActions.TryParse(beat.Key, out var action))
                    _invokeAction(action);
                else
                    _log.LogWarning("Demo: unknown action key '{Key}', skipping.", beat.Key);
                break;

            case "binding":
                if (!string.IsNullOrWhiteSpace(beat.Action))
                    _invokeBinding(beat.Action);
                else
                    _log.LogWarning("Demo: binding beat missing 'action', skipping.");
                break;

            case "type":
                await TypeAsync(beat.Text ?? "", beat.TypeDelayMs ?? script.TypeDelayMs, ct);
                if (beat.Enter)
                    _injectText("\r");
                break;

            case "key":
                var seq = DemoKeys.Resolve(beat.Chord);
                if (seq is not null)
                    _injectText(seq);
                else
                    _log.LogWarning("Demo: unknown key '{Chord}', skipping.", beat.Chord);
                break;

            case "config":
                if (!string.IsNullOrWhiteSpace(beat.Key) && beat.Value is not null)
                    _applyConfig(beat.Key, beat.Value);
                else
                    _log.LogWarning("Demo: config beat needs 'key' and 'value', skipping.");
                break;

            case "wait":
                await DelayWithPauseAsync(beat.Ms ?? 0, ct);
                break;

            default:
                _log.LogWarning("Demo: unknown beat type '{Type}', skipping.", beat.Type);
                break;
        }
    }

    // Type one character at a time so it looks hand-typed. Each char goes
    // through the same SurfaceText path real keystrokes use.
    private async Task TypeAsync(string text, int perCharMs, CancellationToken ct)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            ct.ThrowIfCancellationRequested();
            _injectText(rune.ToString());
            if (perCharMs > 0)
                await Task.Delay(perCharMs, ct);
        }
    }

    private async Task DelayWithPauseAsync(int ms, CancellationToken ct)
    {
        if (ms > 0)
            await Task.Delay(ms, ct);

        // Hold here while paused (auto mode). Poll lightly; pause is a
        // recording convenience, not a hot path.
        while (_paused)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
        }
    }

    private async Task WaitForStepAsync(CancellationToken ct)
    {
        _stepGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => _stepGate.TrySetCanceled()))
        {
            await _stepGate.Task;
        }
    }
}
#endif
