using System;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// Time-boxed single-value cache, ported from the macOS surface's
/// <c>CachedValue</c>. Screen readers poll frequently and each fetch takes the
/// renderer mutex, so we serve a cached value for <c>durationMs</c> before
/// refetching. The clock is injected (<paramref name="nowMs"/>) so expiry is
/// deterministic in tests; production passes <c>Environment.TickCount64</c>.
/// Not thread-safe; call on the UI thread.
/// </summary>
public sealed class CachedValue<T>
{
    private readonly long _durationMs;
    private readonly Func<T> _fetch;
    private readonly Func<long> _nowMs;
    private bool _has;
    private T _value = default!;
    private long _storedAt;

    public CachedValue(long durationMs, Func<T> fetch, Func<long> nowMs)
    {
        _durationMs = durationMs;
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    public T Get()
    {
        var now = _nowMs();
        if (_has && now - _storedAt < _durationMs) return _value;
        _value = _fetch();
        _storedAt = now;
        _has = true;
        return _value;
    }

    public void Invalidate() => _has = false;
}
