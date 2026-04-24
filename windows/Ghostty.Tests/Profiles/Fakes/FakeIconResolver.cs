using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles.Fakes;

/// <summary>
/// Returns canned PNG bytes per IconSpec. If no canned entry, returns
/// a 4-byte sentinel ("FAKE") so tests can verify the resolver was hit.
/// </summary>
internal sealed class FakeIconResolver : IIconResolver
{
    private static readonly byte[] Sentinel = "FAKE"u8.ToArray();
    private readonly Dictionary<IconSpec, byte[]> _canned = new();

    public List<IconSpec> Calls { get; } = new();

    public void EnqueueResult(IconSpec spec, byte[] bytes) => _canned[spec] = bytes;

    public Task<byte[]> ResolveAsync(IconSpec spec, CancellationToken ct)
    {
        Calls.Add(spec);
        return Task.FromResult(_canned.TryGetValue(spec, out var b) ? b : Sentinel);
    }
}
