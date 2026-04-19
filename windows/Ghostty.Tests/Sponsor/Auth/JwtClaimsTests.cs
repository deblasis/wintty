using System;
using System.Text;
using System.Text.Json;
using Ghostty.Core.Sponsor.Auth;
using Xunit;

namespace Ghostty.Tests.Sponsor.Auth;

public class JwtClaimsTests
{
    // Helper: build a minimal test JWT. Signature byte sequence is meaningless;
    // JwtClaims never validates it.
    private static string MakeJwt(object payload)
    {
        static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(
            """{"alg":"HS256","typ":"JWT"}"""));
        var body   = Base64UrlEncode(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(payload)));
        var sig    = Base64UrlEncode(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        return $"{header}.{body}.{sig}";
    }

    [Fact]
    public void Parse_HappyPath_ExtractsAllClaims()
    {
        var jwt = MakeJwt(new
        {
            sub            = "12345",
            login          = "alice",
            tier_cents     = 500,
            channel_allow  = new[] { "stable", "tip" },
            default_channel = (string?)null,
            exp            = 1800000000L,
            iat            = 1799000000L,
            jti            = "abc-123",
        });

        var claims = JwtClaims.Parse(jwt);

        Assert.Equal("12345", claims.Subject);
        Assert.Equal("alice", claims.Login);
        Assert.Equal(500, claims.TierCents);
        Assert.Equal(new[] { "stable", "tip" }, claims.ChannelAllow);
        Assert.Null(claims.DefaultChannel);
        Assert.Equal("abc-123", claims.JwtId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000L).UtcDateTime, claims.ExpiresAt);
    }

    [Fact]
    public void Parse_MissingOptionalClaims_UsesDefaults()
    {
        var jwt = MakeJwt(new { sub = "999", exp = 2000000000L });

        var claims = JwtClaims.Parse(jwt);

        Assert.Equal("999", claims.Subject);
        Assert.Null(claims.Login);
        Assert.Equal(0, claims.TierCents);
        Assert.Empty(claims.ChannelAllow);
        Assert.Null(claims.DefaultChannel);
        Assert.Null(claims.JwtId);
    }

    [Fact]
    public void Parse_PaddingStrippedOnBase64Url_StillDecodes()
    {
        // Payload designed so the base64url encoding requires padding.
        var jwt = MakeJwt(new { sub = "a", exp = 1L });

        var claims = JwtClaims.Parse(jwt);

        Assert.Equal("a", claims.Subject);
    }

    [Fact]
    public void Parse_WrongSegmentCount_Throws()
    {
        Assert.Throws<AuthException>(() => JwtClaims.Parse("only.two"));
        Assert.Throws<AuthException>(() => JwtClaims.Parse("a.b.c.d"));
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => JwtClaims.Parse(""));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var bad = $"{B64("{}")}.{B64("not-json")}.{B64("sig")}";

        var ex = Assert.Throws<AuthException>(() => JwtClaims.Parse(bad));
        Assert.Equal(AuthErrorKind.Unknown, ex.Kind);
    }

    [Fact]
    public void Parse_MissingExp_Throws()
    {
        var jwt = MakeJwt(new { sub = "999" });

        var ex = Assert.Throws<AuthException>(() => JwtClaims.Parse(jwt));
        Assert.Equal(AuthErrorKind.Unknown, ex.Kind);
        Assert.Contains("exp", ex.Message);
    }
}
