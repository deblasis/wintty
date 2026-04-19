using System;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Xunit;

namespace Ghostty.Tests.Sponsor.Auth;

[SupportedOSPlatform("windows")]
public class HttpLoopbackListenerTests
{
    [Fact]
    public async Task HappyPath_TokenAndNonceExtracted()
    {
        using var listener = new HttpLoopbackListener();
        listener.Start();

        using var http = new HttpClient();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            await http.GetAsync($"http://127.0.0.1:{listener.Port}/cb?token=abc&nonce=xyz");
        });

        var result = await listener.AwaitCallbackAsync(CancellationToken.None);

        Assert.Equal("abc", result.Token);
        Assert.Equal("xyz", result.Nonce);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task OAuthErrorQueryParam_Surfaces()
    {
        using var listener = new HttpLoopbackListener();
        listener.Start();

        using var http = new HttpClient();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            await http.GetAsync($"http://127.0.0.1:{listener.Port}/cb?error=access_denied");
        });

        var result = await listener.AwaitCallbackAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal("access_denied", result.Error);
    }

    [Fact]
    public async Task WrongPath_Returns404AndKeepsListening()
    {
        using var listener = new HttpLoopbackListener();
        listener.Start();

        using var http = new HttpClient();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            var wrong = await http.GetAsync($"http://127.0.0.1:{listener.Port}/wrong");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, wrong.StatusCode);

            await Task.Delay(50);
            await http.GetAsync($"http://127.0.0.1:{listener.Port}/cb?token=t&nonce=n");
        });

        var result = await listener.AwaitCallbackAsync(CancellationToken.None);

        Assert.Equal("t", result.Token);
    }

    [Fact]
    public async Task CancellationToken_StopsWait()
    {
        using var listener = new HttpLoopbackListener();
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => listener.AwaitCallbackAsync(cts.Token));
    }

    [Fact]
    public void Port_BeforeStart_Throws()
    {
        using var listener = new HttpLoopbackListener();

        Assert.Throws<InvalidOperationException>(() => _ = listener.Port);
    }
}
