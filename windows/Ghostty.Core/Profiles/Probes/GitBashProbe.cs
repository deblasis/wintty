using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles.Probes;

/// <summary>
/// Probes Git for Windows via HKLM\SOFTWARE\GitForWindows\InstallPath
/// and verifies bin\bash.exe exists. Command uses --login -i to match
/// the launcher that Git for Windows installs on the Start menu.
///
/// When usr\bin\winpty.exe is present (ships with every Git for Windows
/// install since 2.x), wrap the bash invocation with winpty so that
/// bash's job-control init (TIOCSPGRP) sees a MinTTY-compatible PTY
/// instead of ConPTY's bare console. Without the wrap, bash startup
/// emits "cannot set terminal process group (-1): Inappropriate ioctl
/// for device" + "no job control in this shell" warnings on every
/// launch. winpty bridges ConPTY -> MinTTY semantics; this is the
/// pattern Windows Terminal users adopt for the same reason.
/// </summary>
internal sealed class GitBashProbe(IRegistryReader reg, IFileSystem fs) : IInstalledShellProbe
{
    public string ProbeId => "git-bash";

    public Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        var installPath = reg.ReadString(RegistryHive.LocalMachine,
            @"SOFTWARE\GitForWindows", "InstallPath");
        if (string.IsNullOrEmpty(installPath))
            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());

        var bash = Path.Combine(installPath, "bin", "bash.exe");
        if (!fs.FileExists(bash))
            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());

        var winpty = Path.Combine(installPath, "usr", "bin", "winpty.exe");
        var command = fs.FileExists(winpty)
            ? $"{ProbeUtil.QuoteIfNeeded(winpty)} {ProbeUtil.QuoteIfNeeded(bash)} --login -i"
            : $"{ProbeUtil.QuoteIfNeeded(bash)} --login -i";

        var profile = new DiscoveredProfile(
            Id: "git-bash",
            Name: "Git Bash",
            Command: command,
            ProbeId: ProbeId,
            Icon: new IconSpec.BundledKey("git-bash"));

        return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(new[] { profile });
    }
}
