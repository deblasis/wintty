using System;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Xunit;

namespace Ghostty.Tests.Sponsor.Auth;

public class EnvTokenProviderTests
{
    private const string VarName = "WINTTY_DEV_JWT_TEST_PROBE";

    [Fact]
    public async Task GetTokenAsync_WhenEnvVarSet_ReturnsIt()
    {
        Environment.SetEnvironmentVariable(VarName, "eyJ.abc.def");
        try
        {
            var p = new EnvTokenProvider(VarName);
            var token = await p.GetTokenAsync();
            Assert.Equal("eyJ.abc.def", token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VarName, null);
        }
    }

    [Fact]
    public async Task GetTokenAsync_WhenEnvVarUnset_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(VarName, null);
        var p = new EnvTokenProvider(VarName);
        var token = await p.GetTokenAsync();
        Assert.Null(token);
    }

    [Fact]
    public async Task GetTokenAsync_CachesReading()
    {
        Environment.SetEnvironmentVariable(VarName, "initial");
        try
        {
            var p = new EnvTokenProvider(VarName);
            var first = await p.GetTokenAsync();
            // Change env var after construction: must not affect cached value.
            Environment.SetEnvironmentVariable(VarName, "changed");
            var second = await p.GetTokenAsync();
            Assert.Equal("initial", first);
            Assert.Equal("initial", second);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VarName, null);
        }
    }

    [Fact]
    public void Invalidate_IsNoOp()
    {
        Environment.SetEnvironmentVariable(VarName, "x");
        try
        {
            var p = new EnvTokenProvider(VarName);
            p.Invalidate();  // does not throw
        }
        finally
        {
            Environment.SetEnvironmentVariable(VarName, null);
        }
    }

    [Fact]
    public void TokenInvalidated_NeverFires()
    {
        Environment.SetEnvironmentVariable(VarName, "x");
        try
        {
            var p = new EnvTokenProvider(VarName);
            var fired = false;
            p.TokenInvalidated += (_, _) => fired = true;
            p.Invalidate();
            Assert.False(fired);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VarName, null);
        }
    }
}
