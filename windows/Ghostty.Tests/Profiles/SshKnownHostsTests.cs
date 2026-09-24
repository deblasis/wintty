using System.Linq;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public class SshKnownHostsTests
{
    [Fact]
    public void Parse_NamedHost_BecomesSshProfile()
    {
        var p = Assert.Single(SshKnownHosts.Parse("devel.local ssh-ed25519 AAAAC3Nza\n", user: null));

        Assert.Equal("ssh-devel-local", p.Id);
        Assert.Equal("SSH: devel.local", p.Name);
        Assert.Equal("ssh devel.local", p.Command);
        Assert.Equal(SshKnownHosts.ProbeId, p.ProbeId);
        Assert.Equal(new IconSpec.BrandKey("ssh", null), p.Icon);
    }

    [Fact]
    public void Parse_User_PrefixesLogin()
    {
        var p = Assert.Single(SshKnownHosts.Parse("devel.local ssh-ed25519 AAAA", user: "jgill"));
        Assert.Equal("ssh jgill@devel.local", p.Command);
    }

    [Fact]
    public void Parse_UnsafeUser_IsIgnoredNotInjected()
    {
        var p = Assert.Single(SshKnownHosts.Parse("devel.local ssh-ed25519 AAAA", user: "x & calc"));
        Assert.Equal("ssh devel.local", p.Command);
    }

    [Fact]
    public void Parse_AliasesOnOneLine_AreOneHostNamedByTheName()
    {
        var p = Assert.Single(SshKnownHosts.Parse("192.168.0.44,devel.local ssh-ed25519 AAAA", user: null));
        Assert.Equal("SSH: devel.local", p.Name);
    }

    [Fact]
    public void Parse_OneHostPerKeyType_IsListedOnce()
    {
        const string text = """
            devel.local ssh-ed25519 AAAA
            devel.local ssh-rsa BBBB
            devel.local ecdsa-sha2-nistp256 CCCC
            """;
        Assert.Single(SshKnownHosts.Parse(text, user: null));
    }

    [Fact]
    public void Parse_BracketedPort_AddsPortFlag_AndPort22IsPlain()
    {
        const string text = """
            [git.example.com]:2222 ssh-ed25519 AAAA
            [plain.example.com]:22 ssh-ed25519 BBBB
            """;
        var list = SshKnownHosts.Parse(text, user: "me");

        var withPort = Assert.Single(list, p => p.Name == "SSH: git.example.com:2222");
        Assert.Equal("ssh-git-example-com-2222", withPort.Id);
        Assert.Equal("ssh -p 2222 me@git.example.com", withPort.Command);
        var plain = Assert.Single(list, p => p.Name == "SSH: plain.example.com");
        Assert.Equal("ssh me@plain.example.com", plain.Command);
    }

    [Fact]
    public void Parse_SkipsHashedMarkersCommentsWildcardsAndIpOnly()
    {
        const string text = """
            # a comment
            |1|c2FsdA==|aGFzaA== ssh-ed25519 AAAA
            @cert-authority *.example.com ssh-rsa BBBB
            @revoked bad.example.com ssh-rsa CCCC
            *.internal ssh-ed25519 DDDD
            !blocked.example.com ssh-ed25519 EEEE
            192.168.0.10 ssh-ed25519 FFFF
            [10.0.0.5]:2222 ssh-ed25519 GGGG
            fe80::1 ssh-ed25519 HHHH

            keep.example.com ssh-ed25519 IIII
            """;
        var p = Assert.Single(SshKnownHosts.Parse(text, user: null));
        Assert.Equal("SSH: keep.example.com", p.Name);
    }

    [Fact]
    public void Parse_UnsafeHostName_IsSkipped()
    {
        Assert.Empty(SshKnownHosts.Parse("evil&calc ssh-ed25519 AAAA\n-oProxyCommand=x ssh-ed25519 BBBB", user: null));
    }

    [Fact]
    public void Parse_SortsByHost_AndHandlesCrlf()
    {
        var list = SshKnownHosts.Parse("zeta.example ssh-ed25519 A\r\nalpha.example ssh-ed25519 B\r\n", user: null);
        Assert.Equal(new[] { "SSH: alpha.example", "SSH: zeta.example" }, list.Select(p => p.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_NoText_IsEmpty(string? text)
    {
        Assert.Empty(SshKnownHosts.Parse(text, user: "me"));
    }
}
