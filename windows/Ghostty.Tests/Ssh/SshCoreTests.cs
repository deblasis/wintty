using System.Collections.Generic;
using Ghostty.Core.Ssh;
using Xunit;

namespace Ghostty.Tests.Ssh;

public class SshCommandLineTests
{
    [Fact]
    public void PlainDestination()
    {
        var t = SshCommandLine.Parse("ssh jgill@devel.local");
        Assert.NotNull(t);
        Assert.Equal("devel.local", t!.Destination);
        Assert.Equal("jgill", t.User);
        Assert.Null(t.Port);
        Assert.Empty(t.PassThrough);
    }

    [Fact]
    public void QuotedProgramPathWithSpaces()
    {
        var t = SshCommandLine.Parse("\"C:\\Windows\\System32\\OpenSSH\\ssh.exe\" -p 2222 devel");
        Assert.Equal("devel", t!.Destination);
        Assert.Equal(2222, t.Port);
    }

    [Fact]
    public void ClusteredFlagsAndAttachedValue()
    {
        var t = SshCommandLine.Parse("ssh -vAp2200 -l root host");
        Assert.Equal("host", t!.Destination);
        Assert.Equal("root", t.User);
        Assert.Equal(2200, t.Port);
    }

    [Fact]
    public void IdentityJumpAndConfigPassThrough()
    {
        var t = SshCommandLine.Parse("ssh -i \"C:\\Users\\me\\my key\" -J bastion -F cfg -4 host");
        Assert.Equal(["-i", "C:\\Users\\me\\my key", "-J", "bastion", "-F", "cfg", "-4"], t!.PassThrough);
    }

    [Fact]
    public void OptionsKeepConnectionOnesAndDropSessionOnes()
    {
        var t = SshCommandLine.Parse(
            "ssh -o StrictHostKeyChecking=accept-new -o RemoteCommand=tmux -o \"LocalForward 8080 x:80\" -o User=bob -o Port=2022 host");
        Assert.Equal(["-o", "StrictHostKeyChecking=accept-new"], t!.PassThrough);
        Assert.Equal("bob", t.User);
        Assert.Equal(2022, t.Port);
    }

    [Fact]
    public void RemoteCommandAfterDestinationIsIgnored()
    {
        var t = SshCommandLine.Parse("ssh host -p 99 ls -la");
        Assert.Equal("host", t!.Destination);
        Assert.Null(t.Port);
    }

    [Fact]
    public void SshUriCarriesUserAndPort()
    {
        var t = SshCommandLine.Parse("ssh ssh://jgill@devel.local:2222");
        Assert.Equal("devel.local", t!.Destination);
        Assert.Equal("jgill", t.User);
        Assert.Equal(2222, t.Port);
    }

    [Fact]
    public void ExplicitLoginBeatsDestinationLogin()
    {
        var t = SshCommandLine.Parse("ssh -l admin jgill@host");
        Assert.Equal("admin", t!.User);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ssh")]
    [InlineData("ssh -V")]
    [InlineData("ssh -p")]
    [InlineData("ssh -p notaport host")]
    [InlineData("ssh -Z host")]
    [InlineData("scp a host:b")]
    [InlineData("pwsh.exe -NoLogo")]
    public void NotAnSshSession(string? commandLine)
        => Assert.Null(SshCommandLine.Parse(commandLine));

    [Fact]
    public void Ipv6DestinationIsBracketedInRemotePrefix()
    {
        var t = SshCommandLine.Parse("ssh -l me fe80::1");
        Assert.Equal("me@[fe80::1]:", t!.RemotePrefix);
    }

    [Theory]
    [InlineData("a b", new[] { "a", "b" })]
    [InlineData("\"a b\" c", new[] { "a b", "c" })]
    [InlineData("a\\\\b", new[] { "a\\\\b" })]
    [InlineData("\"a\\\"b\"", new[] { "a\"b" })]
    [InlineData("\"a\\\\\" b", new[] { "a\\", "b" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    public void SplitWindowsFollowsTheCrtRules(string line, string[] expected)
        => Assert.Equal(expected, SshCommandLine.SplitWindows(line));
}

public class RemotePathTests
{
    [Theory]
    [InlineData("jgill@devel: ~/src/wintty", "~/src/wintty")]
    [InlineData("jgill@devel:~/src", "~/src")]
    [InlineData("jgill@devel:/var/log", "/var/log")]
    [InlineData("devel: ~", "~")]
    [InlineData("root@box: /", "/")]
    [InlineData("  jgill@devel: /srv/my files  ", "/srv/my files")]
    [InlineData("~/projects", "~/projects")]
    [InlineData("/etc", "/etc")]
    public void DirectoryFromTitle_Recognised(string title, string expected)
        => Assert.Equal(expected, RemotePath.DirectoryFromTitle(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nvim README.md")]
    [InlineData("C:\\Users\\jnngi")]
    [InlineData("Administrator: Windows PowerShell")]
    [InlineData("jgill@devel")]
    [InlineData("~ - fish")]
    [InlineData("user@host: relative/dir")]
    public void DirectoryFromTitle_NotADirectory(string? title)
        => Assert.Null(RemotePath.DirectoryFromTitle(title));

    [Theory]
    [InlineData("~/src", "a.txt", "~/src/a.txt")]
    [InlineData("~/src/", "a.txt", "~/src/a.txt")]
    [InlineData("/var", "./log/x", "/var/log/x")]
    [InlineData("~/src", "/etc/hosts", "/etc/hosts")]
    [InlineData("~/src", "~/other", "~/other")]
    [InlineData(null, "a.txt", "a.txt")]
    public void Combine(string? dir, string name, string expected)
        => Assert.Equal(expected, RemotePath.Combine(dir, name));

    [Theory]
    [InlineData("~", ".")]
    [InlineData("~/", ".")]
    [InlineData("~/src/x", "src/x")]
    [InlineData("/abs", "/abs")]
    [InlineData("rel/x", "rel/x")]
    [InlineData("~bob/x", "~bob/x")]
    public void ForSftp(string path, string expected)
        => Assert.Equal(expected, RemotePath.ForSftp(path));

    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("report[1].txt", "report\\[1\\].txt")]
    [InlineData("a*b?c", "a\\*b\\?c")]
    public void EscapeGlob(string path, string expected)
        => Assert.Equal(expected, RemotePath.EscapeGlob(path));

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("a\u001bb")]
    [InlineData("")]
    [InlineData(null)]
    public void ControlCharactersAreInvalid(string? path)
        => Assert.False(RemotePath.IsValid(path));

    [Theory]
    [InlineData("script.sh*", "script.sh")]
    [InlineData("link@", "link")]
    [InlineData("dir/", "dir")]
    [InlineData("'quoted name'", "quoted name")]
    [InlineData("  notes.md ", "notes.md")]
    [InlineData("/", "/")]
    public void CandidateFromWord(string word, string expected)
        => Assert.Equal(expected, RemotePath.CandidateFromWord(word));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    public void CandidateFromWord_Nothing(string? word)
        => Assert.Null(RemotePath.CandidateFromWord(word));

    [Theory]
    [InlineData("~/src/a.txt", "a.txt")]
    [InlineData("/var/log/", "log")]
    [InlineData("a.txt", "a.txt")]
    public void FileName(string path, string expected)
        => Assert.Equal(expected, RemotePath.FileName(path));
}

public class ScpCommandTests
{
    private static readonly SshTarget Target =
        new("devel.local", "jgill", 2222, ["-i", "C:\\keys\\id"]);

    [Fact]
    public void Upload_BatchIntoHomeRelativeDirectory()
    {
        var args = ScpCommand.Upload(Target, ["C:\\tmp\\a b.txt", "C:\\tmp\\c.txt"], "~/src", recursive: false, batch: true);
        Assert.Equal(
            ["-s", "-B", "-P", "2222", "-i", "C:\\keys\\id", "--", "C:\\tmp\\a b.txt", "C:\\tmp\\c.txt", "jgill@devel.local:src/"],
            args);
    }

    [Fact]
    public void Upload_VisibleRecursiveIntoHome()
    {
        var args = ScpCommand.Upload(new SshTarget("box"), ["C:\\d"], "~", recursive: true, batch: false);
        Assert.Equal(["-s", "-r", "--", "C:\\d", "box:./"], args);
    }

    [Fact]
    public void Download_EscapesGlobAndAlwaysForcesSftp()
    {
        var args = ScpCommand.Download(new SshTarget("box"), "/data/report[1].txt", "C:\\Users\\me\\Downloads", batch: true);
        Assert.Equal(["-s", "-B", "-r", "--", "box:/data/report\\[1\\].txt", "C:\\Users\\me\\Downloads"], args);
    }

    [Fact]
    public void ShellMetacharactersStayOneArgument()
    {
        // With -s the name is data; this pins that it is never split or
        // rewritten on the way to scp.
        var args = ScpCommand.Download(new SshTarget("box"), "x;reboot $(id)", "C:\\d", batch: true);
        Assert.Contains("box:x;reboot $(id)", args);
        Assert.Equal("-s", args[0]);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("")]
    public void InvalidRemotePathsThrow(string path)
    {
        Assert.Throws<System.ArgumentException>(() => ScpCommand.Download(Target, path, "C:\\d", true));
        Assert.Throws<System.ArgumentException>(() => ScpCommand.Upload(Target, ["C:\\a"], path, false, true));
    }

    [Fact]
    public void CommandLineRoundTripsThroughTheWindowsSplitter()
    {
        IReadOnlyList<string> args =
            ["-s", "--", "C:\\my files\\a \"q\".txt", "C:\\dir\\", "C:\\sp ace\\", "", "box:x y/"];
        var line = ScpCommand.ToCommandLine("C:\\Program Files\\scp.exe", args);
        var split = SshCommandLine.SplitWindows(line);
        Assert.Equal("C:\\Program Files\\scp.exe", split[0]);
        Assert.Equal(args, [.. System.Linq.Enumerable.Skip(split, 1)]);
    }

    [Theory]
    [InlineData("jgill@devel.local: Permission denied (publickey,password).", ScpFailureKind.NeedsInteraction)]
    [InlineData("Host key verification failed.", ScpFailureKind.NeedsInteraction)]
    [InlineData("scp: /nope/x: No such file or directory", ScpFailureKind.Other)]
    [InlineData("scp: dest open \"/root/x\": Permission denied", ScpFailureKind.Other)]
    [InlineData("", ScpFailureKind.Other)]
    public void FailureClassification(string stderr, ScpFailureKind expected)
        => Assert.Equal(expected, ScpFailure.Classify(stderr));
}

public class SshProcessFinderTests
{
    [Fact]
    public void FindsSshUnderTheShell()
    {
        ProcessEntry[] snap = [new(10, 1, "pwsh.exe"), new(11, 10, "conhost.exe"), new(12, 10, "ssh.exe")];
        Assert.Equal(12u, SshProcessFinder.FindSsh(snap, 10));
    }

    [Fact]
    public void TheShellItselfCanBeSsh()
    {
        ProcessEntry[] snap = [new(10, 1, "ssh.exe")];
        Assert.Equal(10u, SshProcessFinder.FindSsh(snap, 10));
    }

    [Fact]
    public void OutermostSshWinsOverAJumpHostChild()
    {
        ProcessEntry[] snap =
        [
            new(10, 1, "cmd.exe"), new(30, 20, "ssh.exe"), new(20, 10, "ssh.exe"),
        ];
        Assert.Equal(20u, SshProcessFinder.FindSsh(snap, 10));
    }

    [Fact]
    public void OtherPanesSshIsNotOurs()
    {
        ProcessEntry[] snap = [new(10, 1, "pwsh.exe"), new(50, 1, "pwsh.exe"), new(51, 50, "ssh.exe")];
        Assert.Null(SshProcessFinder.FindSsh(snap, 10));
    }

    [Fact]
    public void PidCyclesTerminate()
    {
        ProcessEntry[] snap = [new(10, 11, "a.exe"), new(11, 10, "b.exe")];
        Assert.Null(SshProcessFinder.FindSsh(snap, 10));
    }
}

public class SshConnectionParserTests
{
    private static Dictionary<string, List<string>> Pairs(params (string Key, string Value)[] lines)
    {
        var d = new Dictionary<string, List<string>>();
        foreach (var (k, v) in lines)
        {
            if (!d.TryGetValue(k, out var list)) d[k] = list = [];
            list.Add(v);
        }
        return d;
    }

    [Fact]
    public void FullConnection()
    {
        var r = SshConnectionParser.Parse(Pairs(
            ("ssh.devel.host", "devel.local"),
            ("ssh.devel.user", "jgill"),
            ("ssh.devel.port", "2222"),
            ("ssh.devel.identity-file", "~/.ssh/id_ed25519"),
            ("ssh.devel.jump-host", "me@bastion:2200"),
            ("ssh.devel.forward-agent", "true"),
            ("ssh.devel.name", "Devel box"),
            ("font-size", "12")));
        Assert.Empty(r.Warnings);
        var c = Assert.Single(r.Connections);
        Assert.Equal("ssh-devel", c.ProfileId);
        Assert.Equal("Devel box", c.DisplayName);
        Assert.Equal(
            "ssh -p 2222 -i \"C:\\Users\\me\\.ssh\\id_ed25519\" -J me@bastion:2200 -A jgill@devel.local",
            c.Command("C:\\Users\\me"));
    }

    [Fact]
    public void MinimalConnectionAndDefaultName()
    {
        var c = Assert.Single(SshConnectionParser.Parse(Pairs(("ssh.box.host", "box"), ("ssh.box.port", "22"))).Connections);
        Assert.Equal("ssh box", c.Command());
        Assert.Equal("SSH: box", c.DisplayName);
        Assert.Null(c.Port);
    }

    [Fact]
    public void LastValueWins()
    {
        var c = Assert.Single(SshConnectionParser.Parse(Pairs(("ssh.box.host", "old"), ("ssh.box.host", "new"))).Connections);
        Assert.Equal("new", c.Host);
    }

    [Theory]
    [InlineData("user", "bob")]
    [InlineData("host", "-oProxyCommand=calc")]
    [InlineData("host", "a b")]
    [InlineData("host", "a;b")]
    [InlineData("user", "bob&calc")]
    [InlineData("port", "0")]
    [InlineData("port", "70000")]
    [InlineData("identity-file", "C:\\k\" & calc & \"")]
    [InlineData("identity-file", "%USERPROFILE%\\k")]
    [InlineData("jump-host", "a b")]
    [InlineData("forward-agent", "maybe")]
    public void BadValuesDropTheConnectionWithAWarning(string subKey, string value)
    {
        var lines = new List<(string, string)> { ("ssh.x." + subKey, value) };
        if (subKey != "host") lines.Add(("ssh.x.host", subKey == "user" && value == "bob" ? "" : "box"));
        var r = SshConnectionParser.Parse(Pairs([.. lines]));
        Assert.Empty(r.Connections);
        Assert.Single(r.Warnings);
        Assert.StartsWith("ssh 'x':", r.Warnings[0]);
    }

    [Fact]
    public void ToConfigRoundTrips()
    {
        var original = new SshConnection("devel", "devel.local", "Devel", "jgill", 2222, "~/.ssh/id", "bastion", true);
        var pairs = new Dictionary<string, List<string>>();
        foreach (var (k, v) in SshConnectionParser.ToConfig(original)) pairs[k] = [v];
        Assert.Equal(original, Assert.Single(SshConnectionParser.Parse(pairs).Connections));
    }

    [Theory]
    [InlineData("ssh.a.host", true)]
    [InlineData("SSH.My-Box.identity-file", true)]
    [InlineData("ssh-hosts-discovery", false)]
    [InlineData("ssh.a", false)]
    [InlineData("ssh..host", false)]
    public void IsConnectionKey(string key, bool expected)
        => Assert.Equal(expected, SshConnectionParser.IsConnectionKey(key));

    [Fact]
    public void SuggestIdIsSlugAndUnique()
    {
        Assert.Equal("devel-local", SshConnectionParser.SuggestId("Devel.Local", []));
        Assert.Equal("devel-local-2", SshConnectionParser.SuggestId("devel.local", ["devel-local"]));
        Assert.Equal("host", SshConnectionParser.SuggestId("::", []));
    }
}

public class SshConfigHostsTests
{
    [Fact]
    public void ConcreteAliasesOnly()
    {
        const string text = """
            # comment
            Host devel build-box
              HostName devel.local
              User jgill
            Host *.example.com !bad
              ForwardAgent yes
            Host=eq
            Match host x
            host lower
            Host "quoted"
            Host -oProxyCommand=calc
            """;
        var profiles = SshConfigHosts.Parse(text);
        Assert.Equal(
            ["ssh-build-box", "ssh-devel", "ssh-eq", "ssh-lower", "ssh-quoted"],
            [.. System.Linq.Enumerable.Select(profiles, p => p.Id)]);
        Assert.Equal("ssh devel", profiles[1].Command);
        Assert.Equal("SSH: devel", profiles[1].Name);
    }

    [Fact]
    public void EmptyOrMissing()
    {
        Assert.Empty(SshConfigHosts.Parse(null));
        Assert.Empty(SshConfigHosts.Parse(""));
    }
}
