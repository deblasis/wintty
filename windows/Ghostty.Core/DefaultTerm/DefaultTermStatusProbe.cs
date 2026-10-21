namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// One +defterm-status reading.
/// </summary>
/// <param name="SelectionOwned">
/// The Console delegation pair names this build's CLSIDs: what the
/// registration engine calls IsRegistered.
/// </param>
/// <param name="Kind">The class-object kind THIS process actually holds
/// right now. Never the intended state.</param>
/// <param name="TerminalClsid">This build's embedded terminal CLSID.</param>
/// <param name="ConsoleClsid">This build's derived console CLSID.</param>
/// <param name="ProxyPresent">The proxy DLL beside this exe exists.</param>
/// <param name="ProxyHash">The proxy's reported hash, or "unpinned".</param>
public sealed record DefaultTermStatusReport(
    bool SelectionOwned,
    RegistrationKind Kind,
    string TerminalClsid,
    string ConsoleClsid,
    bool ProxyPresent,
    string ProxyHash);

/// <summary>
/// The headless +defterm-status probe: what is embedded, what is
/// registered, what this process holds, and whether the machine is
/// healthy. Healthy means nothing is broken: an unregistered machine is
/// healthy; a machine whose selection names this build with the proxy
/// missing is not, because the handoff path cannot work there.
/// </summary>
public sealed class DefaultTermStatusProbe
{
    private readonly string _terminalClsid;
    private readonly string _consoleClsid;
    private readonly Func<bool> _isRegistered;
    private readonly Func<RegistrationKind> _kind;
    private readonly Func<bool> _proxyPresent;
    private readonly Func<string?> _proxyHash;

    public DefaultTermStatusProbe(
        string terminalClsid,
        string consoleClsid,
        Func<bool> isRegistered,
        Func<RegistrationKind> kind,
        Func<bool> proxyPresent,
        Func<string?> proxyHash)
    {
        _terminalClsid = terminalClsid;
        _consoleClsid = consoleClsid;
        _isRegistered = isRegistered;
        _kind = kind;
        _proxyPresent = proxyPresent;
        _proxyHash = proxyHash;
    }

    public DefaultTermStatusReport Report() => new(
        SelectionOwned: _isRegistered(),
        Kind: _kind(),
        TerminalClsid: _terminalClsid,
        ConsoleClsid: _consoleClsid,
        ProxyPresent: _proxyPresent(),
        ProxyHash: _proxyHash() ?? "unpinned");

    /// <summary>0 healthy, 1 broken.</summary>
    public static int ExitCodeFor(DefaultTermStatusReport report) =>
        report.SelectionOwned && !report.ProxyPresent ? 1 : 0;

    /// <summary>
    /// The stable final line scripts pin. Field order and spellings are
    /// load-bearing for tooling; change them only with a deprecation
    /// window.
    /// </summary>
    public string MachineLine(DefaultTermStatusReport report) =>
        "defterm-status: "
        + "selection=" + (report.SelectionOwned ? "owned" : "unowned")
        + " kind=" + KindWord(report.Kind)
        + " terminal=" + report.TerminalClsid
        + " console=" + report.ConsoleClsid
        + " proxy=" + (report.ProxyPresent ? "present" : "missing");

    public string Render(DefaultTermStatusReport report) => string.Join(
        Environment.NewLine,
        "default terminal status",
        "  selection    " + (report.SelectionOwned ? "this build owns the delegation pair" : "not this build"),
        "  class object " + KindWord(report.Kind) + " (actual, this process)",
        "  terminal clsid " + report.TerminalClsid,
        "  console  clsid " + report.ConsoleClsid,
        "  proxy        " + (report.ProxyPresent ? "present" : "missing") + " hash " + report.ProxyHash,
        MachineLine(report));

    private static string KindWord(RegistrationKind kind) => kind switch
    {
        RegistrationKind.Permanent => "permanent",
        RegistrationKind.Transient => "transient",
        _ => "none",
    };
}
