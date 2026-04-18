using Ghostty.Core.Sponsor.Update;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public class UpdateCheckExceptionTests
{
    [Fact]
    public void Constructor_PopulatesKindDetailAndInner()
    {
        var inner = new System.InvalidOperationException("boom");
        var ex = new UpdateCheckException(UpdateErrorKind.ServerError, "503", inner);

        Assert.Equal(UpdateErrorKind.ServerError, ex.Kind);
        Assert.Equal("503", ex.Detail);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Constructor_InnerIsOptional()
    {
        var ex = new UpdateCheckException(UpdateErrorKind.NoToken, null);
        Assert.Equal(UpdateErrorKind.NoToken, ex.Kind);
        Assert.Null(ex.Detail);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Message_IncludesKindForDiagnostics()
    {
        var ex = new UpdateCheckException(UpdateErrorKind.AuthExpired, "token 12345");
        Assert.Contains("AuthExpired", ex.Message);
    }
}
