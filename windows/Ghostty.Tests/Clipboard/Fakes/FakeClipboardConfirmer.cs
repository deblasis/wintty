using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;

namespace Ghostty.Tests.Clipboard.Fakes;

/// <summary>
/// Hand-written fake of IClipboardConfirmer. Returns pre-programmed
/// responses in FIFO order and records every call. Defaults to Denied
/// when the response queue runs out, matching the production safety
/// default.
/// </summary>
internal sealed class FakeClipboardConfirmer : IClipboardConfirmer
{
    private readonly Queue<ClipboardConfirmResult> _responses = new();

    public List<(ClipboardConfirmSnapshot Snapshot, ClipboardConfirmRequest Request, IntPtr OriginSurface)> Calls { get; } = new();

    /// <summary>The preview text each call would have rendered, for brevity in assertions.</summary>
    public IReadOnlyList<string> Previews =>
        Calls.ConvertAll(c => c.Snapshot.PreviewText);

    public void EnqueueResponse(bool accept) =>
        _responses.Enqueue(accept ? ClipboardConfirmResult.Allow() : ClipboardConfirmResult.Denied);

    public void EnqueueResponse(ClipboardConfirmResult result) => _responses.Enqueue(result);

    public ValueTask<ClipboardConfirmResult> ConfirmAsync(
        ClipboardConfirmSnapshot snapshot,
        ClipboardConfirmRequest request,
        IntPtr originSurface)
    {
        Calls.Add((snapshot, request, originSurface));
        var result = _responses.Count > 0 ? _responses.Dequeue() : ClipboardConfirmResult.Denied;
        return new ValueTask<ClipboardConfirmResult>(result);
    }
}
