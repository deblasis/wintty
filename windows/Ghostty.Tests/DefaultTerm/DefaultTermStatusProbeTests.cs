using Ghostty.Core.DefaultTerm;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The +defterm-status probe: it reports the ACTUAL registration state
/// (the class-object kind this process holds), never the intended one,
/// its exit code separates healthy from broken, and its final line is a
/// stable machine-readable format scripts can pin. All inputs injected:
/// no test reads the registry, a file, or a live class object.
/// </summary>
public sealed class DefaultTermStatusProbeTests
{
    [Fact]
    public void ReportsActualKind_NotIntended()
    {
        // Selection says this build owns the delegation pair (the
        // intended state), but the process holds no class object: the
        // probe must say kind=none anyway.
        var probe = NewProbe(
            isRegistered: () => true,
            kind: () => RegistrationKind.None);

        var report = probe.Report();

        Assert.True(report.SelectionOwned);
        Assert.Equal(RegistrationKind.None, report.Kind);
        Assert.Contains("kind=none", probe.MachineLine(report));
    }

    [Fact]
    public void ExitCodes()
    {
        // Registered with everything present: healthy.
        Assert.Equal(0, DefaultTermStatusProbe.ExitCodeFor(NewProbe(
            isRegistered: () => true,
            kind: () => RegistrationKind.Permanent,
            proxyPresent: () => true).Report()));

        // Registered but the proxy is gone: broken, nonzero.
        Assert.Equal(1, DefaultTermStatusProbe.ExitCodeFor(NewProbe(
            isRegistered: () => true,
            kind: () => RegistrationKind.Permanent,
            proxyPresent: () => false).Report()));

        // Not registered: an unregistered machine is a healthy machine.
        Assert.Equal(0, DefaultTermStatusProbe.ExitCodeFor(NewProbe(
            isRegistered: () => false,
            kind: () => RegistrationKind.None,
            proxyPresent: () => false).Report()));
    }

    [Fact]
    public void DeterministicLineForScripting()
    {
        var probe = NewProbe(
            isRegistered: () => true,
            kind: () => RegistrationKind.Transient);

        var line = probe.MachineLine(probe.Report());

        Assert.Equal(
            "defterm-status: selection=owned kind=transient terminal={11111111-1111-1111-1111-111111111111} console={22222222-2222-2222-2222-222222222222} proxy=present",
            line);
    }

    private static DefaultTermStatusProbe NewProbe(
        Func<bool> isRegistered,
        Func<RegistrationKind> kind,
        Func<bool>? proxyPresent = null) => new(
        terminalClsid: "{11111111-1111-1111-1111-111111111111}",
        consoleClsid: "{22222222-2222-2222-2222-222222222222}",
        isRegistered: isRegistered,
        kind: kind,
        proxyPresent: proxyPresent ?? (() => true),
        proxyHash: () => "unpinned");
}
