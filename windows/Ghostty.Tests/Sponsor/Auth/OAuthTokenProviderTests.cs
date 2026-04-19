using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Sponsor.Auth;

public partial class OAuthTokenProviderTests
{
    // ---------------------------------------------------------------
    // Test doubles shared across partial class files. Later tasks add
    // more test methods but use the same fakes.
    // ---------------------------------------------------------------

    internal sealed class FakeStore : IJwtStore
    {
        public byte[]? Bytes;
        public int Writes; public int Reads; public int Deletes;
        public Task<byte[]?> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(Bytes); }
        public Task WriteAsync(byte[] utf8Token, CancellationToken ct) { Writes++; Bytes = utf8Token; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Deletes++; Bytes = null; return Task.CompletedTask; }
    }

    internal sealed class FakeBrowser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = new();
        public void Open(Uri url) => Opened.Add(url);
    }

    internal sealed class FakeListener : ILoopbackListener
    {
        public int Port => 54321;
        public bool Started;
        public Func<CancellationToken, Task<LoopbackResult>>? Behavior;
        public void Start() { Started = true; }
        public Task<LoopbackResult> AwaitCallbackAsync(CancellationToken ct)
            => Behavior?.Invoke(ct) ?? Task.FromResult(new LoopbackResult(null, null, null));
        public void Dispose() { }
    }

    internal sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    // Build helper. Returns all the fakes so individual tests can drive them.
    internal static (OAuthTokenProvider provider, FakeStore store, FakeBrowser browser, FakeListener listener, FakeHandler handler, FakeTime time) Build(string? envOverride = null)
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.wintty.io") };
        var auth = new WinttyAuthClient(http, new Uri("https://api.wintty.io"));
        var store = new FakeStore();
        var browser = new FakeBrowser();
        var listener = new FakeListener();
        var time = new FakeTime();
        var provider = new OAuthTokenProvider(
            auth, store, browser, listener,
            new Uri("https://api.wintty.io"),
            NullLogger<OAuthTokenProvider>.Instance,
            time,
            envVarLookup: _ => envOverride);
        return (provider, store, browser, listener, handler, time);
    }

    // JWT helper: build a test JWT expiring `secondsFromNow` in the future.
    internal static string MakeJwt(FakeTime time, int secondsFromNow)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = time.Now.AddSeconds(secondsFromNow).ToUnixTimeSeconds();
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var body   = B64(Encoding.UTF8.GetBytes($$"""{"sub":"u","exp":{{exp}},"jti":"j"}"""));
        var sig    = B64(new byte[] { 1, 2, 3 });
        return $"{header}.{body}.{sig}";
    }

    [Fact]
    public async Task GetTokenAsync_WhenNoCache_ReturnsNull()
    {
        var (provider, _, _, _, _, _) = Build();

        var token = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (provider, _, _, _, _, _) = Build();

        provider.Dispose();
        provider.Dispose();
    }

    // InitializeAsync

    [Fact]
    public async Task InitializeAsync_WithEnvVar_LoadsVerbatimSkipsStore()
    {
        var time = new FakeTime { Now = DateTimeOffset.UtcNow };
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        var (provider, store, _, _, _, _) = Build(envOverride: jwt);

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Equal(jwt, await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task InitializeAsync_WithNoStoredBlob_StaysNoToken()
    {
        var (provider, store, _, _, _, _) = Build();
        store.Bytes = null;

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task InitializeAsync_WithValidStoredBlob_LoadsIntoCache()
    {
        var (provider, store, _, _, _, time) = Build();
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        store.Bytes = Encoding.UTF8.GetBytes(jwt);

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Equal(jwt, await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsync_WithExpiredButRefreshableBlob_RefreshesAndReplaces()
    {
        var (provider, store, _, _, handler, time) = Build();
        var oldJwt = MakeJwt(time, secondsFromNow: -3600);
        store.Bytes = Encoding.UTF8.GetBytes(oldJwt);
        var newJwt = MakeJwt(time, secondsFromNow: 3600);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"token":"{{newJwt}}"}""",
                Encoding.UTF8, "application/json"),
        };

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Equal(newJwt, await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Writes);
        Assert.Equal(newJwt, Encoding.UTF8.GetString(store.Bytes!));
    }

    [Fact]
    public async Task InitializeAsync_WithStaleBlobPast24h_DeletesAndStaysNoToken()
    {
        var (provider, store, _, _, _, time) = Build();
        var stale = MakeJwt(time, secondsFromNow: -48 * 3600);
        store.Bytes = Encoding.UTF8.GetBytes(stale);

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Deletes);
    }

    [Fact]
    public async Task InitializeAsync_WithMalformedBlob_DeletesAndStaysNoToken()
    {
        var (provider, store, _, _, _, _) = Build();
        store.Bytes = Encoding.UTF8.GetBytes("not-a-jwt");

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Deletes);
    }

    [Fact]
    public async Task InitializeAsync_RefreshFailsUnauthorized_DeletesAndStaysNoToken()
    {
        var (provider, store, _, _, handler, time) = Build();
        var expired = MakeJwt(time, secondsFromNow: -60);
        store.Bytes = Encoding.UTF8.GetBytes(expired);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Deletes);
    }

    // SignInAsync

    private static string? GetQueryParam(Uri url, string key)
    {
        var q = url.Query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            if (Uri.UnescapeDataString(pair[..idx]) == key)
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return null;
    }

    [Fact]
    public async Task SignInAsync_HappyPath_EchoesNoncePersists()
    {
        var (provider, store, browser, listener, _, time) = Build();
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        listener.Behavior = _ =>
        {
            var opened = browser.Opened[^1];
            var nonce = GetQueryParam(opened, "nonce")!;
            return Task.FromResult(new LoopbackResult(jwt, nonce, null));
        };

        var result = await provider.SignInAsync(CancellationToken.None);

        Assert.True(result);
        Assert.Equal(jwt, await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Writes);
        Assert.Single(browser.Opened);
        Assert.Equal("api.wintty.io", browser.Opened[0].Host);
        Assert.Equal("/auth/github/start", browser.Opened[0].AbsolutePath);
    }

    [Fact]
    public async Task SignInAsync_StartUrlUsesLoopbackAndNonceQueryShape()
    {
        var (provider, _, browser, listener, _, time) = Build();
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        listener.Behavior = _ =>
        {
            var opened = browser.Opened[^1];
            var nonce = GetQueryParam(opened, "nonce")!;
            return Task.FromResult(new LoopbackResult(jwt, nonce, null));
        };

        await provider.SignInAsync(CancellationToken.None);

        var opened = browser.Opened[0];
        // Contract with the Worker: ?loopback=<port>&nonce=<hex>.
        Assert.Equal("54321", GetQueryParam(opened, "loopback"));
        Assert.Matches(@"^[0-9A-Fa-f]{32}$", GetQueryParam(opened, "nonce"));
        Assert.Null(GetQueryParam(opened, "redirect"));
    }

    [Fact]
    public async Task SignInAsync_NonceMismatch_ReturnsFalse()
    {
        var (provider, _, _, listener, _, time) = Build();
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        listener.Behavior = _ => Task.FromResult(new LoopbackResult(jwt, "wrong-nonce", null));

        var result = await provider.SignInAsync(CancellationToken.None);

        Assert.False(result);
        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SignInAsync_ListenerCanceled_ReturnsFalse()
    {
        var (provider, _, _, listener, _, _) = Build();
        listener.Behavior = ct => Task.FromException<LoopbackResult>(new OperationCanceledException(ct));

        var result = await provider.SignInAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task SignInAsync_OAuthErrorQueryParam_ReturnsFalse()
    {
        var (provider, _, _, listener, _, _) = Build();
        listener.Behavior = _ => Task.FromResult(new LoopbackResult(null, null, "access_denied"));

        var result = await provider.SignInAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task SignInAsync_ReturnedJwtPreExpired_ReturnsFalseDoesNotPersist()
    {
        var (provider, store, browser, listener, _, time) = Build();
        var expired = MakeJwt(time, secondsFromNow: -10);
        listener.Behavior = _ =>
        {
            var nonce = GetQueryParam(browser.Opened[^1], "nonce")!;
            return Task.FromResult(new LoopbackResult(expired, nonce, null));
        };

        var result = await provider.SignInAsync(CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task SignInAsync_TokenMissing_ReturnsFalse()
    {
        var (provider, _, browser, listener, _, _) = Build();
        listener.Behavior = _ =>
        {
            var nonce = GetQueryParam(browser.Opened[^1], "nonce")!;
            return Task.FromResult(new LoopbackResult(null, nonce, null));
        };

        var result = await provider.SignInAsync(CancellationToken.None);

        Assert.False(result);
    }

    // Invalidate (reactive refresh)

        [Fact]
        public async Task Invalidate_RefreshSucceeds_ReplacesCacheDoesNotFire()
        {
            var (provider, store, _, _, handler, time) = Build();
            var current = MakeJwt(time, secondsFromNow: 60);
            store.Bytes = Encoding.UTF8.GetBytes(current);
            await provider.InitializeAsync(CancellationToken.None);

            var refreshed = MakeJwt(time, secondsFromNow: 7 * 24 * 3600);
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"token":"{{refreshed}}"}""",
                    Encoding.UTF8, "application/json"),
            };

            var fired = 0;
            provider.TokenInvalidated += (_, _) => fired++;

            provider.Invalidate();
            await Task.Delay(200); // wait for background refresh

            Assert.Equal(refreshed, await provider.GetTokenAsync(CancellationToken.None));
            Assert.Equal(0, fired);
        }

        [Fact]
        public async Task Invalidate_RefreshFailsUnauthorized_ClearsAndFires()
        {
            var (provider, store, _, _, handler, time) = Build();
            var current = MakeJwt(time, secondsFromNow: 60);
            store.Bytes = Encoding.UTF8.GetBytes(current);
            await provider.InitializeAsync(CancellationToken.None);

            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var fired = 0;
            provider.TokenInvalidated += (_, _) => fired++;

            provider.Invalidate();
            await Task.Delay(200);

            Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
            Assert.Equal(1, fired);
            Assert.Equal(1, store.Deletes);
        }

        [Fact]
        public async Task Invalidate_RefreshFailsTransient_KeepsCacheDoesNotFire()
        {
            var (provider, store, _, _, handler, time) = Build();
            var current = MakeJwt(time, secondsFromNow: 60);
            store.Bytes = Encoding.UTF8.GetBytes(current);
            await provider.InitializeAsync(CancellationToken.None);

            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

            var fired = 0;
            provider.TokenInvalidated += (_, _) => fired++;

            provider.Invalidate();
            await Task.Delay(200);

            Assert.Equal(current, await provider.GetTokenAsync(CancellationToken.None));
            Assert.Equal(0, fired);
        }

        [Fact]
        public async Task Invalidate_InEnvVarMode_IsNoOp()
        {
            var time = new FakeTime();
            var jwt = MakeJwt(time, secondsFromNow: 3600);
            var (provider, _, _, _, handler, _) = Build(envOverride: jwt);
            await provider.InitializeAsync(CancellationToken.None);

            var fired = 0;
            provider.TokenInvalidated += (_, _) => fired++;

            provider.Invalidate();
            await Task.Delay(100);

            Assert.Equal(jwt, await provider.GetTokenAsync(CancellationToken.None));
            Assert.Equal(0, fired);
            Assert.Empty(handler.Requests); // no refresh call
        }

    // Proactive refresh timer

    /// <summary>
    /// TimeProvider that lets tests advance virtual time and triggers
    /// timers registered via CreateTimer deterministically.
    /// </summary>
    internal sealed class DeterministicTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = new();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Schedule(dueTime);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            Now = Now.Add(by);
            // Snapshot + remove fired timers; fire callbacks after the lock.
            var due = new List<FakeTimer>();
            lock (_timers)
            {
                for (int i = _timers.Count - 1; i >= 0; i--)
                {
                    if (_timers[i].DueAt is { } at && at <= Now)
                    {
                        due.Add(_timers[i]);
                        _timers.RemoveAt(i);
                    }
                }
            }
            foreach (var t in due) t.Fire();
        }

        internal void Register(FakeTimer timer)
        {
            lock (_timers)
            {
                _timers.RemoveAll(t => ReferenceEquals(t, timer));
                _timers.Add(timer);
            }
        }

        internal void Unregister(FakeTimer timer)
        {
            lock (_timers) { _timers.RemoveAll(t => ReferenceEquals(t, timer)); }
        }
    }

    internal sealed class FakeTimer : ITimer
    {
        private readonly DeterministicTimeProvider _time;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        public DateTimeOffset? DueAt { get; private set; }

        public FakeTimer(DeterministicTimeProvider time, TimerCallback cb, object? state)
        {
            _time = time; _callback = cb; _state = state;
        }

        public void Schedule(TimeSpan dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan) { DueAt = null; _time.Unregister(this); return; }
            DueAt = _time.GetUtcNow().Add(dueTime);
            _time.Register(this);
        }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period) { Schedule(dueTime); return true; }

        public void Dispose() { _time.Unregister(this); DueAt = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private static (OAuthTokenProvider provider, FakeStore store, FakeBrowser browser, FakeListener listener, FakeHandler handler, TimeProvider time) BuildWithTime(TimeProvider time)
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.wintty.io") };
        var auth = new WinttyAuthClient(http, new Uri("https://api.wintty.io"));
        var store = new FakeStore();
        var browser = new FakeBrowser();
        var listener = new FakeListener();
        var provider = new OAuthTokenProvider(
            auth, store, browser, listener,
            new Uri("https://api.wintty.io"),
            NullLogger<OAuthTokenProvider>.Instance,
            time,
            envVarLookup: _ => null);
        return (provider, store, browser, listener, handler, time);
    }

    private static string MakeJwtExplicit(DateTimeOffset expAt)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var body   = B64(Encoding.UTF8.GetBytes($$"""{"sub":"u","exp":{{expAt.ToUnixTimeSeconds()}},"jti":"j"}"""));
        var sig    = B64(new byte[] { 1, 2, 3 });
        return $"{header}.{body}.{sig}";
    }

    [Fact]
    public async Task ProactiveRefresh_AtMinus24h_Succeeds()
    {
        var time = new DeterministicTimeProvider();
        var (provider, store, _, _, handler, _) = BuildWithTime(time);
        var current = MakeJwtExplicit(time.Now.AddHours(48));
        store.Bytes = Encoding.UTF8.GetBytes(current);
        await provider.InitializeAsync(CancellationToken.None);

        var refreshed = MakeJwtExplicit(time.Now.AddDays(7));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"token":"{{refreshed}}"}""",
                Encoding.UTF8, "application/json"),
        };

        provider.StartRefreshTimer();

        time.Advance(TimeSpan.FromHours(25)); // cross exp - 24h
        await Task.Delay(200);

        Assert.Equal(refreshed, await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProactiveRefresh_TransientFailure_ReschedulesAt10m()
    {
        var time = new DeterministicTimeProvider();
        var (provider, store, _, _, handler, _) = BuildWithTime(time);
        var current = MakeJwtExplicit(time.Now.AddHours(25));
        store.Bytes = Encoding.UTF8.GetBytes(current);
        await provider.InitializeAsync(CancellationToken.None);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        provider.StartRefreshTimer();

        time.Advance(TimeSpan.FromHours(2)); // past exp-24h
        await Task.Delay(200);

        Assert.Equal(current, await provider.GetTokenAsync(CancellationToken.None));

        var refreshed = MakeJwtExplicit(time.Now.AddDays(7));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"token":"{{refreshed}}"}""",
                Encoding.UTF8, "application/json"),
        };
        time.Advance(TimeSpan.FromMinutes(11));
        await Task.Delay(200);

        Assert.Equal(refreshed, await provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProactiveRefresh_AuthFailure_ClearsAndFiresInvalidated()
    {
        var time = new DeterministicTimeProvider();
        var (provider, store, _, _, handler, _) = BuildWithTime(time);
        var current = MakeJwtExplicit(time.Now.AddHours(25));
        store.Bytes = Encoding.UTF8.GetBytes(current);
        await provider.InitializeAsync(CancellationToken.None);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var fired = 0;
        provider.TokenInvalidated += (_, _) => fired++;

        provider.StartRefreshTimer();
        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(200);

        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, fired);
        Assert.Equal(1, store.Deletes);
    }

    // SignOutAsync

    [Fact]
    public async Task SignOutAsync_RevokeSucceeds_DeletesLocallyAndFires()
    {
        var (provider, store, _, _, handler, time) = Build();
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        store.Bytes = Encoding.UTF8.GetBytes(jwt);
        await provider.InitializeAsync(CancellationToken.None);

        var fired = 0;
        provider.TokenInvalidated += (_, _) => fired++;

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        await provider.SignOutAsync(CancellationToken.None);

        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Deletes);
        Assert.Equal(1, fired);
        Assert.Single(handler.Requests);
        Assert.Equal("/auth/revoke", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SignOutAsync_RevokeFails_StillDeletesLocally()
    {
        var (provider, store, _, _, handler, time) = Build();
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        store.Bytes = Encoding.UTF8.GetBytes(jwt);
        await provider.InitializeAsync(CancellationToken.None);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        await provider.SignOutAsync(CancellationToken.None);

        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, store.Deletes);
    }

    [Fact]
    public async Task SignOutAsync_WhenNotSignedIn_IsNoOp()
    {
        var (provider, store, _, _, handler, _) = Build();

        await provider.SignOutAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Equal(0, store.Deletes);
    }

    [Fact]
    public async Task SignOutAsync_EnvVarMode_SkipsRevokeAndDelete()
    {
        var time = new FakeTime();
        var jwt = MakeJwt(time, secondsFromNow: 3600);
        var (provider, store, _, _, handler, _) = Build(envOverride: jwt);
        await provider.InitializeAsync(CancellationToken.None);

        await provider.SignOutAsync(CancellationToken.None);

        // Env-var path: no revoke call, no store delete (there's nothing
        // on disk to delete in env-var mode anyway). Cache clears locally.
        Assert.Empty(handler.Requests);
        Assert.Equal(0, store.Deletes);
        Assert.Null(await provider.GetTokenAsync(CancellationToken.None));
    }
    }
