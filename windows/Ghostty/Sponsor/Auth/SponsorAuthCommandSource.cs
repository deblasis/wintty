#if SPONSOR_BUILD
using System;
using System.Collections.Generic;
using Ghostty.Commands;
using Ghostty.Core.Sponsor.Auth;
using Microsoft.Extensions.Logging;

namespace Ghostty.Sponsor.Auth;

/// <summary>
/// Palette entries for OAuth sign-in / sign-out. Visible in release
/// sponsor builds (not DEBUG-gated like <c>SponsorUpdateCommandSource</c>).
/// Tracks sign-in state via <c>TokenAcquired</c> / <c>TokenInvalidated</c>
/// (which can fire on a ThreadPool thread from the reactive / proactive
/// refresh paths) with a volatile flag, and computes the visible entry
/// lazily inside <see cref="GetCommands"/> on the UI thread. This avoids
/// mutating a shared list off-thread - the palette calls
/// <see cref="ICommandSource.Refresh"/> then <see cref="GetCommands"/> on
/// each Open.
/// </summary>
internal sealed class SponsorAuthCommandSource : ICommandSource, IDisposable
{
    private readonly OAuthTokenProvider _provider;
    private readonly ILogger<SponsorAuthCommandSource> _logger;
    private volatile bool _hasToken;
    private bool _disposed;

    public SponsorAuthCommandSource(OAuthTokenProvider provider, ILogger<SponsorAuthCommandSource> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);
        _provider = provider;
        _logger = logger;

        _provider.TokenAcquired    += OnTokenAcquired;
        _provider.TokenInvalidated += OnTokenInvalidated;

        // Seed from the provider's current state. After construction
        // _hasToken is kept in sync by the event handlers above.
        _hasToken = provider.HasToken;
    }

    public IReadOnlyList<CommandItem> GetCommands()
    {
        // Called on the UI thread by CommandPaletteViewModel.Open.
        // Reads _hasToken (volatile) and returns a fresh list - no
        // shared mutable buffer to race against.
        if (_hasToken)
        {
            return new[]
            {
                new CommandItem
                {
                    Id = "wintty.signOut",
                    Title = "Sign out of wintty",
                    Description = "Stops sponsor updates on this machine",
                    Category = CommandCategory.Custom,
                    Execute = _ => SignOutFireAndForget(),
                },
            };
        }

        return new[]
        {
            new CommandItem
            {
                Id = "wintty.signIn",
                Title = "Sign in to wintty",
                Description = "Activate sponsor updates via GitHub",
                Category = CommandCategory.Custom,
                Execute = _ => SignInFireAndForget(),
            },
        };
    }

    public void Refresh()
    {
        // No-op: state lives in _hasToken, which is kept in sync by the
        // provider's events. GetCommands rebuilds the item list each
        // call anyway.
    }

    private void SignInFireAndForget()
    {
        // Fire-and-forget: the palette close happens on the caller's
        // UI thread. The provider's sign-in flow runs async; if it fails
        // we log but show nothing.
        _ = SignInAsync();
    }

    private async System.Threading.Tasks.Task SignInAsync()
    {
        try { await _provider.SignInAsync(System.Threading.CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "[sponsor/auth] palette SignIn failed"); }
    }

    private void SignOutFireAndForget()
    {
        _ = SignOutAsync();
    }

    private async System.Threading.Tasks.Task SignOutAsync()
    {
        try { await _provider.SignOutAsync(System.Threading.CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "[sponsor/auth] palette SignOut failed"); }
    }

    private void OnTokenAcquired(object? sender, EventArgs e) => _hasToken = true;
    private void OnTokenInvalidated(object? sender, EventArgs e) => _hasToken = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.TokenAcquired    -= OnTokenAcquired;
        _provider.TokenInvalidated -= OnTokenInvalidated;
    }
}
#endif
