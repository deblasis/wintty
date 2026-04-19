using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Xunit;

namespace Ghostty.Tests.Sponsor.Auth;

public class WinttyAuthClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static (WinttyAuthClient client, StubHandler handler) Build()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.wintty.io") };
        var client = new WinttyAuthClient(http, new Uri("https://api.wintty.io"));
        return (client, handler);
    }

    [Fact]
    public async Task RefreshAsync_HappyPath_ReturnsNewTokenWithBearer()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"token":"new-jwt-value"}""",
                Encoding.UTF8, "application/json"),
        };

        var result = await client.RefreshAsync("old-jwt", CancellationToken.None);

        Assert.Equal("new-jwt-value", result);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/auth/refresh", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("old-jwt", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task RefreshAsync_401_ThrowsUnauthorized()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.RefreshAsync("old", CancellationToken.None));

        Assert.Equal(AuthErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task RefreshAsync_403_ThrowsUnauthorized()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.RefreshAsync("old", CancellationToken.None));

        Assert.Equal(AuthErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task RefreshAsync_500_ThrowsServerError()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.RefreshAsync("old", CancellationToken.None));

        Assert.Equal(AuthErrorKind.ServerError, ex.Kind);
    }

    [Fact]
    public async Task RefreshAsync_NetworkFailure_ThrowsNetwork()
    {
        var (client, handler) = Build();
        handler.Respond = _ => throw new HttpRequestException("dns fail");

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.RefreshAsync("old", CancellationToken.None));

        Assert.Equal(AuthErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task RefreshAsync_MalformedJson_ThrowsServerError()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        };

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.RefreshAsync("old", CancellationToken.None));

        Assert.Equal(AuthErrorKind.ServerError, ex.Kind);
    }

    [Fact]
    public async Task RefreshAsync_MissingTokenField_ThrowsServerError()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"unexpected":"shape"}""",
                Encoding.UTF8, "application/json"),
        };

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.RefreshAsync("old", CancellationToken.None));

        Assert.Equal(AuthErrorKind.ServerError, ex.Kind);
    }

    [Fact]
    public async Task RevokeAsync_HappyPath_Completes()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        await client.RevokeAsync("jwt", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/auth/revoke", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("jwt", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task RevokeAsync_500_Throws()
    {
        var (client, handler) = Build();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.RevokeAsync("jwt", CancellationToken.None));

        Assert.Equal(AuthErrorKind.ServerError, ex.Kind);
    }
}
