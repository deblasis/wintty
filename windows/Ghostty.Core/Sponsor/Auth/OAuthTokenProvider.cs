using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Production <see cref="ISponsorTokenProvider"/> backed by the GitHub
/// OAuth loopback flow and a DPAPI-encrypted JWT cache. Replaces
/// <see cref="EnvTokenProvider"/> in the DI root for SPONSOR_BUILD
/// output, with WINTTY_DEV_JWT preserved as a short-circuit
/// override for dev smoke paths.
/// </summary>
internal sealed partial class OAuthTokenProvider : ISponsorTokenProvider, IDisposable
{
    private readonly WinttyAuthClient _auth;
    private readonly IJwtStore _store;
    private readonly IBrowserLauncher _browser;
    private readonly ILoopbackListener _listener;
    private readonly Uri _apiBase;
    private readonly ILogger<OAuthTokenProvider> _logger;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _envVarLookup;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private volatile string? _cached;
    private JwtClaims? _claims;
    private bool _envVarMode;
    private bool _disposed;

    private ITimer? _refreshTimer;
    private static readonly TimeSpan RefreshLead    = TimeSpan.FromHours(24);
    private static readonly TimeSpan TransientRetry = TimeSpan.FromMinutes(10);

    public OAuthTokenProvider(
        WinttyAuthClient auth,
        IJwtStore store,
        IBrowserLauncher browser,
        ILoopbackListener listener,
        Uri apiBase,
        ILogger<OAuthTokenProvider> logger,
        TimeProvider time,
        Func<string, string?>? envVarLookup = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(apiBase);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);

        _auth = auth;
        _store = store;
        _browser = browser;
        _listener = listener;
        _apiBase = apiBase;
        _logger = logger;
        _time = time;
        _envVarLookup = envVarLookup ?? Environment.GetEnvironmentVariable;
    }

    public Task<string?> GetTokenAsync(CancellationToken ct = default)
        => Task.FromResult(_cached);

    /// <summary>
    /// Snapshot view of whether we currently hold a token. Read from the
    /// UI thread by palette command sources that need to pick between
    /// "Sign in" and "Sign out" entries without blocking on
    /// <see cref="GetTokenAsync"/>. Volatile-backed via <c>_cached</c>.
    /// </summary>
    public bool HasToken => !string.IsNullOrEmpty(_cached);

    /// <summary>
    /// Reactive 401 handler called by <c>WinttyManifestClient</c> when
    /// the Worker rejects our current bearer. Kicks off a single-flight
    /// background refresh. Returns synchronously so the calling HTTP
    /// retry can proceed. In env-var mode this is a no-op (no refresh
    /// path exists for env tokens). If another refresh is already in
    /// flight, this caller's retry rides on its result.
    /// </summary>
    public void Invalidate()
    {
        if (_envVarMode) return;
        if (_disposed) return;
        // Capture the token before Task.Run schedules; reading
        // _lifetime.Token after Dispose has disposed the CTS throws
        // ObjectDisposedException, and we can't observe the exception
        // from a fire-and-forget Task.Run.
        CancellationToken token;
        try { token = _lifetime.Token; }
        catch (ObjectDisposedException) { return; }
        _ = Task.Run(() => TryRefreshAsync(token));
    }

    private async Task TryRefreshAsync(CancellationToken ct)
    {
        if (_disposed) return;
        try
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                // Another refresh already in flight; let it handle things.
                return;
            }
        }
        catch (ObjectDisposedException) { return; }

        var fireInvalidated = false;
        try
        {
            var current = _cached;
            if (string.IsNullOrEmpty(current)) return;

            try
            {
                var refreshed = await _auth.RefreshAsync(current, ct).ConfigureAwait(false);
                var parsed = JwtClaims.Parse(refreshed);
                await _store.WriteAsync(Encoding.UTF8.GetBytes(refreshed), ct).ConfigureAwait(false);
                _cached = refreshed;
                _claims = parsed;
                LogReactiveRefreshSucceeded();
                ScheduleNextRefresh();
            }
            catch (AuthException ex) when (ex.Kind == AuthErrorKind.Unauthorized)
            {
                LogReactiveRefreshRejected();
                _cached = null;
                _claims = null;
                await SafeDeleteAsync(ct).ConfigureAwait(false);
                fireInvalidated = true;
            }
            catch (AuthException ex)
            {
                LogReactiveRefreshTransient(ex);
                // Leave cache alone; next 401 will retry.
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            LogReactiveRefreshUnexpected(ex);
        }
        finally
        {
            _gate.Release();
        }

        // Raise outside the gate so a subscriber that re-enters the
        // provider (e.g. kicking off SignIn) does not deadlock on its
        // own WaitAsync.
        if (fireInvalidated)
            TokenInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? TokenInvalidated;

    /// <summary>
    /// Raised after a successful sign-in or proactive/reactive refresh
    /// acquires a new token. The shell-side palette source subscribes
    /// to re-register the "Sign out" entry visibility.
    /// </summary>
    public event EventHandler? TokenAcquired;

    // Internal helper for subsequent tasks that will invoke the event.
    private void RaiseTokenAcquired() => TokenAcquired?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Called once at app launch from <c>SponsorOverlayBootstrapper</c>.
    /// Loads any cached JWT from the store, validates shape + freshness,
    /// attempts a synchronous refresh if the token is expired-but-within-
    /// 24h, and deletes the blob on any unrecoverable state. Does NOT
    /// schedule the proactive refresh timer - that's Task 12's job so
    /// tests can assert boot behavior deterministically without wall
    /// clock interference.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Env-var short-circuit: dev path, never refreshes, skips store.
            var env = _envVarLookup("WINTTY_DEV_JWT");
            if (!string.IsNullOrEmpty(env))
            {
                try
                {
                    _claims = JwtClaims.Parse(env);
                    _cached = env;
                    _envVarMode = true;
                    LogDevJwtLoaded(_claims.ExpiresAt);
                }
                catch (AuthException ex)
                {
                    LogDevJwtMalformed(ex);
                }
                return;
            }

            byte[]? blob;
            try
            {
                blob = await _store.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStoreReadFailed(ex);
                blob = null;
            }

            if (blob is null || blob.Length == 0)
            {
                LogNoCachedJwt();
                return;
            }

            var jwt = Encoding.UTF8.GetString(blob);
            JwtClaims parsed;
            try
            {
                parsed = JwtClaims.Parse(jwt);
            }
            catch (AuthException ex)
            {
                LogCachedJwtMalformed(ex);
                await SafeDeleteAsync(ct).ConfigureAwait(false);
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            if (parsed.ExpiresAt <= now - TimeSpan.FromHours(24))
            {
                LogCachedJwtStale();
                await SafeDeleteAsync(ct).ConfigureAwait(false);
                return;
            }

            if (parsed.ExpiresAt <= now)
            {
                try
                {
                    var refreshed = await _auth.RefreshAsync(jwt, ct).ConfigureAwait(false);
                    var newClaims = JwtClaims.Parse(refreshed);
                    await _store.WriteAsync(Encoding.UTF8.GetBytes(refreshed), ct).ConfigureAwait(false);
                    _cached = refreshed;
                    _claims = newClaims;
                    LogBootRefreshSucceeded();
                    ScheduleNextRefresh();
                    return;
                }
                catch (AuthException ex) when (ex.Kind == AuthErrorKind.Unauthorized)
                {
                    LogBootRefreshRejected();
                    await SafeDeleteAsync(ct).ConfigureAwait(false);
                    return;
                }
                catch (AuthException ex)
                {
                    LogBootRefreshFailed(ex);
                    await SafeDeleteAsync(ct).ConfigureAwait(false);
                    return;
                }
            }

            _cached = jwt;
            _claims = parsed;
            LogLoadedCachedJwt(parsed.ExpiresAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SafeDeleteAsync(CancellationToken ct)
    {
        try { await _store.DeleteAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { LogStoreDeleteFailed(ex); }
    }

    /// <summary>
    /// Starts the proactive refresh timer after a successful
    /// InitializeAsync or SignInAsync. No-op in env-var mode (no refresh
    /// path) and when there is no cached token.
    /// </summary>
    public void StartRefreshTimer()
    {
        if (_envVarMode) return;
        if (_claims is null) return;
        ScheduleNextRefresh();
    }

    private void ScheduleNextRefresh()
    {
        if (_disposed) return;
        if (_claims is null) return;
        var now = _time.GetUtcNow();
        var due = _claims.ExpiresAt - RefreshLead - now.UtcDateTime;
        if (due <= TimeSpan.Zero) due = TimeSpan.Zero;

        _refreshTimer?.Dispose();
        if (_disposed) return;  // double-check after the tiny window between the Dispose call above and here
        _refreshTimer = _time.CreateTimer(
            _ => _ = OnRefreshTickAsync(),
            state: null,
            dueTime: due,
            period: Timeout.InfiniteTimeSpan);
    }

    private async Task OnRefreshTickAsync()
    {
        if (_disposed) return;
        CancellationToken token;
        try { token = _lifetime.Token; }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            {
                return; // another refresh already running; it will reschedule
            }
        }
        catch (ObjectDisposedException) { return; }

        var fireInvalidated = false;
        try
        {
            var current = _cached;
            if (string.IsNullOrEmpty(current)) return;

            try
            {
                var refreshed = await _auth.RefreshAsync(current, token).ConfigureAwait(false);
                var parsed = JwtClaims.Parse(refreshed);
                await _store.WriteAsync(Encoding.UTF8.GetBytes(refreshed), token).ConfigureAwait(false);
                _cached = refreshed;
                _claims = parsed;
                LogProactiveRefreshSucceeded();
                ScheduleNextRefresh();
            }
            catch (AuthException ex) when (ex.Kind == AuthErrorKind.Unauthorized)
            {
                LogProactiveRefreshRejected();
                _cached = null; _claims = null;
                await SafeDeleteAsync(token).ConfigureAwait(false);
                _refreshTimer?.Dispose();
                _refreshTimer = null;
                fireInvalidated = true;
            }
            catch (AuthException ex)
            {
                LogProactiveRefreshTransient(ex);
                _refreshTimer?.Dispose();
                if (_disposed) return;
                _refreshTimer = _time.CreateTimer(
                    _ => _ = OnRefreshTickAsync(), null,
                    TransientRetry, Timeout.InfiniteTimeSpan);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            LogProactiveRefreshUnexpected(ex);
        }
        finally
        {
            _gate.Release();
        }

        if (fireInvalidated)
            TokenInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Revokes the current session best-effort against the Worker, then
    /// deletes the local blob. Fires <see cref="TokenInvalidated"/>
    /// only if there was a token to sign out of. Network/server failures
    /// on revoke are swallowed - the JWT remains valid on the Worker
    /// until <c>exp</c> but the local machine no longer has it.
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var fireInvalidated = false;
        try
        {
            var current = _cached;
            _cached = null;
            _claims = null;
            _refreshTimer?.Dispose();
            _refreshTimer = null;

            if (!string.IsNullOrEmpty(current))
            {
                if (!_envVarMode)
                {
                    try
                    {
                        await _auth.RevokeAsync(current, ct).ConfigureAwait(false);
                        LogRevokeAcknowledged();
                    }
                    catch (Exception ex)
                    {
                        LogRevokeBestEffortFailed(ex);
                    }

                    await SafeDeleteAsync(ct).ConfigureAwait(false);
                }

                fireInvalidated = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (fireInvalidated)
            TokenInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private static readonly TimeSpan LoopbackTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Runs the one-shot OAuth loopback flow: starts the listener,
    /// opens the browser, awaits the callback, validates nonce and
    /// JWT shape, persists. Returns true on success. Returns false
    /// on timeout, user cancel, error query, nonce mismatch, or
    /// pre-expired JWT. Unexpected exceptions propagate.
    /// </summary>
    public async Task<bool> SignInAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var fireAcquired = false;
        try
        {
            try
            {
                _listener.Start();
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException
                                          or System.Net.Sockets.SocketException)
            {
                LogLoopbackBindFailed(ex);
                return false;
            }

            // The Worker's /auth/github/start endpoint takes a `nonce` query param
            // and echoes it verbatim in the final loopback 302. This is an ADDITIONAL
            // CSRF defense on top of the OAuth `state` param (which the Worker
            // owns end-to-end and HS256-signs with its own secret). The client
            // never touches OAuth `state`; our `nonce` is the client-local replay
            // guard against a rogue local process racing the real callback.
            var nonce = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

            var url = new Uri(
                $"{_apiBase.GetLeftPart(UriPartial.Authority)}/auth/github/start"
                + $"?loopback={_listener.Port}"
                + $"&nonce={nonce}");
            _browser.Open(url);

            using var timeoutCts = new CancellationTokenSource(LoopbackTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            LoopbackResult callbackResult;
            try
            {
                callbackResult = await _listener.AwaitCallbackAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogSignInTimedOut();
                return false;
            }

            if (!string.IsNullOrEmpty(callbackResult.Error))
            {
                // Log length only - never log raw attacker-supplied query strings.
                LogOAuthErrorQuery(callbackResult.Error.Length);
                return false;
            }

            if (callbackResult.Nonce != nonce)
            {
                LogNonceMismatch();
                return false;
            }

            if (string.IsNullOrEmpty(callbackResult.Token))
            {
                LogCallbackMissingToken();
                return false;
            }

            JwtClaims parsed;
            try
            {
                parsed = JwtClaims.Parse(callbackResult.Token);
            }
            catch (AuthException ex)
            {
                LogCallbackMalformedJwt(ex);
                return false;
            }

            if (parsed.ExpiresAt <= _time.GetUtcNow().UtcDateTime)
            {
                LogCallbackJwtPreExpired();
                return false;
            }

            await _store.WriteAsync(
                Encoding.UTF8.GetBytes(callbackResult.Token), ct).ConfigureAwait(false);
            _cached = callbackResult.Token;
            _claims = parsed;
            LogSignInComplete(parsed.ExpiresAt);
            fireAcquired = true;
            return true;
        }
        finally
        {
            _gate.Release();

            // Raise outside the gate: a subscriber (e.g. a palette command
            // source) that re-enters the provider on state change must not
            // deadlock on its own WaitAsync.
            if (fireAcquired)
                RaiseTokenAcquired();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Dispose racing with a callback already inside Cancel() can
        // observe ObjectDisposedException; all other throws indicate a
        // real bug and should not be swallowed.
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
        _refreshTimer?.Dispose();
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
