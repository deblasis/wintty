using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;

namespace Ghostty.Tests.Clipboard.Fakes;

/// <summary>
/// Hand-written fake of IClipboardBackend. Records the last write,
/// supplies a queued read response, and can simulate read failures
/// (clipboard locked by another process).
/// </summary>
internal sealed class FakeClipboardBackend : IClipboardBackend
{
    public string? StoredText { get; set; }

    public IReadOnlyList<ClipboardPayload>? LastWrite { get; private set; }
    public int WriteCallCount { get; private set; }

    public Func<string?>? OnRead { get; set; }

    public ValueTask<string?> ReadTextAsync()
    {
        if (OnRead is not null)
            return new ValueTask<string?>(OnRead());
        return new ValueTask<string?>(StoredText);
    }

    public ValueTask WriteAsync(IReadOnlyList<ClipboardPayload> payloads)
    {
        LastWrite = payloads;
        WriteCallCount++;
        return ValueTask.CompletedTask;
    }
}
