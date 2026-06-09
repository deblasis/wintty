using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles.Probes;

/// <summary>
/// Probes for an MSYS2 install at its canonical roots (C:\msys64, then
/// the legacy 32-bit C:\msys32) and surfaces its bash as a profile.
///
/// Detection is path-based, not registry-based: MSYS2 has no documented
/// stable registry key (no HKLM\SOFTWARE\MSYS2), and its uninstaller
/// lives under enumerable Uninstall\* subkeys that IRegistryReader does
/// not expose. The official installer and `winget install MSYS2.MSYS2`
/// both target C:\msys64, so trusting the well-known path mirrors how
/// CmdProbe / PowerShellProbe locate their shells.
///
/// MSYS2 shares Git for Windows' MSYS2/Cygwin bash runtime, so we reuse
/// GitBashProbe's exact launch shape: wrap with usr\bin\winpty.exe when
/// present so bash's job-control init sees a MinTTY-compatible PTY under
/// ConPTY instead of emitting "cannot set terminal process group" + "no
/// job control" warnings. `--login -i` sources /etc/profile, which sets
/// up the default MSYS environment (prompt, PATH, UTF-8 locale). winpty,
/// NOT mintty: mintty is a GUI terminal that would open its own window.
/// </summary>
internal sealed class Msys2Probe(IFileSystem fs) : IInstalledShellProbe
{
    public string ProbeId => "msys2";

    // Ordered by preference: 64-bit default first, legacy 32-bit second.
    private static readonly string[] Roots = { @"C:\msys64", @"C:\msys32" };

    public Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        foreach (var root in Roots)
        {
            var bash = Path.Combine(root, "usr", "bin", "bash.exe");
            if (!fs.FileExists(bash)) continue;

            var winpty = Path.Combine(root, "usr", "bin", "winpty.exe");
            var command = fs.FileExists(winpty)
                ? $"{ProbeUtil.QuoteIfNeeded(winpty)} {ProbeUtil.QuoteIfNeeded(bash)} --login -i"
                : $"{ProbeUtil.QuoteIfNeeded(bash)} --login -i";

            var profile = new DiscoveredProfile(
                Id: "msys2",
                Name: "MSYS2",
                Command: command,
                ProbeId: ProbeId,
                Icon: new IconSpec.BundledKey("bash"));

            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(new[] { profile });
        }

        return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());
    }
}
