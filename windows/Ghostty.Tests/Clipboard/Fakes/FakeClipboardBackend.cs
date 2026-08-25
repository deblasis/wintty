using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// What the fake clipboard is holding, in preference order. Reads are
    /// served from here so a test can set up a multi-format clipboard
    /// without a real DataPackageView.
    /// </summary>
    public List<ClipboardPayload> Stored { get; } = new();

    /// <summary>Counts reads, so a listing can be shown not to perform one.</summary>
    public int ReadCallCount { get; private set; }

    public int AvailableCallCount { get; private set; }

    /// <summary>Set to throw from the multi-format read path.</summary>
    public Func<Exception>? OnReadThrow { get; set; }

    public ValueTask<string?> ReadTextAsync()
    {
        if (OnRead is not null)
            return new ValueTask<string?>(OnRead());
        return new ValueTask<string?>(StoredText);
    }

    public ValueTask<IReadOnlyList<string>> GetAvailableMimesAsync()
    {
        AvailableCallCount++;
        if (OnReadThrow is not null) throw OnReadThrow();

        IReadOnlyList<string> mimes = Stored.Select(p => p.Mime).ToList();
        return new ValueTask<IReadOnlyList<string>>(mimes);
    }

    public ValueTask<IReadOnlyList<ClipboardPayload>> ReadAsync(IReadOnlyList<string> accepted)
    {
        ReadCallCount++;
        if (OnReadThrow is not null) throw OnReadThrow();

        var filter = accepted.Count == 0
            ? null
            : new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ClipboardPayload> result = Stored
            .Where(p => filter is null || filter.Contains(p.Mime))
            .ToList();

        return new ValueTask<IReadOnlyList<ClipboardPayload>>(result);
    }

    public ValueTask WriteAsync(IReadOnlyList<ClipboardPayload> payloads)
    {
        LastWrite = payloads;
        WriteCallCount++;
        return ValueTask.CompletedTask;
    }
}
