using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;

namespace Ghostty.Tests.Clipboard.Fakes;

/// <summary>
/// Hand-written fake of IClipboardConfirmer. Returns pre-programmed
/// responses in FIFO order; records every call as (preview, request, origin).
/// Defaults to "false" (deny) when the response queue runs out, matching
/// the production safety default.
/// </summary>
internal sealed class FakeClipboardConfirmer : IClipboardConfirmer
{
    private readonly Queue<bool> _responses = new();

    public List<(string Preview, ClipboardConfirmRequest Request, IntPtr OriginSurface)> Calls { get; } = new();

    public void EnqueueResponse(bool accept) => _responses.Enqueue(accept);

    public ValueTask<bool> ConfirmAsync(string preview, ClipboardConfirmRequest request, IntPtr originSurface)
    {
        Calls.Add((preview, request, originSurface));
        var result = _responses.Count > 0 ? _responses.Dequeue() : false;
        return new ValueTask<bool>(result);
    }
}
