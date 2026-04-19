using Ghostty.Core.Sponsor.Auth;
using Xunit;

namespace Ghostty.Tests.Sponsor.Auth;

public class AuthExceptionTests
{
    [Fact]
    public void Constructor_PopulatesKindDetailAndInner()
    {
        var inner = new System.Net.Http.HttpRequestException("dns fail");
        var ex = new AuthException(AuthErrorKind.Unauthorized, "revoked jti", inner);

        Assert.Equal(AuthErrorKind.Unauthorized, ex.Kind);
        Assert.Equal("revoked jti", ex.Detail);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Constructor_InnerIsOptional()
    {
        var ex = new AuthException(AuthErrorKind.Network, null);

        Assert.Equal(AuthErrorKind.Network, ex.Kind);
        Assert.Null(ex.Detail);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Message_IncludesKindForDiagnostics()
    {
        var ex = new AuthException(AuthErrorKind.ServerError, "500");

        Assert.Contains("ServerError", ex.Message);
    }
}
