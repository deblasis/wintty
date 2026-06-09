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
///
/// Beyond the base MSYS environment we also surface MSYS2's modern
/// toolchain subsystems (UCRT64, MINGW64, CLANG64) as their own profiles
/// when their per-environment launcher (e.g. <c>ucrt64.exe</c>) is present
/// in the install root. Each variant runs the same bash but selects the
/// subsystem by setting <c>MSYSTEM</c> via coreutils <c>env</c> (which
/// /etc/profile reads to wire up the right PATH), mirroring what
/// <c>msys2_shell.cmd -ucrt64</c> does. Legacy 32-bit subsystems
/// (mingw32/clang32) are intentionally not surfaced.
/// </summary>
internal sealed class Msys2Probe(IFileSystem fs) : IInstalledShellProbe
{
    public string ProbeId => "msys2";

    // Ordered by preference: 64-bit default first, legacy 32-bit second.
    private static readonly string[] Roots = { @"C:\msys64", @"C:\msys32" };

    // Modern toolchain subsystems, in display order. Each is detected by
    // its root launcher "<key>.exe" and selected at runtime via MSYSTEM.
    private static readonly (string Msystem, string Suffix)[] Subsystems =
    {
        ("UCRT64", "ucrt64"),
        ("MINGW64", "mingw64"),
        ("CLANG64", "clang64"),
    };

    public Task<IReadOnlyList<DiscoveredProfile>> DiscoverAsync(CancellationToken ct)
    {
        foreach (var root in Roots)
        {
            var bash = Path.Combine(root, "usr", "bin", "bash.exe");
            if (!fs.FileExists(bash)) continue;

            var winpty = Path.Combine(root, "usr", "bin", "winpty.exe");
            var winptyPrefix = fs.FileExists(winpty)
                ? ProbeUtil.QuoteIfNeeded(winpty) + " "
                : "";
            var bashSuffix = $"{ProbeUtil.QuoteIfNeeded(bash)} --login -i";

            var profiles = new List<DiscoveredProfile>
            {
                new(
                    Id: "msys2",
                    Name: "MSYS2",
                    Command: $"{winptyPrefix}{bashSuffix}",
                    ProbeId: ProbeId,
                    Icon: new IconSpec.BundledKey("bash")),
            };

            // Subsystem variants require coreutils `env` to set MSYSTEM.
            // It ships with the runtime alongside bash; guard defensively.
            var env = Path.Combine(root, "usr", "bin", "env.exe");
            if (fs.FileExists(env))
            {
                var envQuoted = ProbeUtil.QuoteIfNeeded(env);
                foreach (var (msystem, suffix) in Subsystems)
                {
                    if (!fs.FileExists(Path.Combine(root, suffix + ".exe"))) continue;

                    profiles.Add(new DiscoveredProfile(
                        Id: $"msys2-{suffix}",
                        Name: $"MSYS2 {msystem}",
                        Command: $"{winptyPrefix}{envQuoted} MSYSTEM={msystem} {bashSuffix}",
                        ProbeId: ProbeId,
                        Icon: new IconSpec.BundledKey("bash")));
                }
            }

            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(profiles);
        }

        return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(System.Array.Empty<DiscoveredProfile>());
    }
}
