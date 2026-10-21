using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// The identity values the registration engine needs, stated so tests can
/// inject throwaway GUIDs instead of this build's real ones.
/// </summary>
public interface IDefaultTermIdentityView
{
    /// <summary>The CLSID the shell resolves for this terminal. Braced GUID string.</summary>
    string TerminalClsid { get; }

    /// <summary>The CLSID installed on the console class. Braced GUID string.</summary>
    string ConsoleClsid { get; }

    /// <summary>The CLSID of the console proxy in the handoff chain. Braced GUID string.</summary>
    string ProxyClsid { get; }

    /// <summary>The IIDs the handoff chain negotiates with the proxy, in negotiation order.</summary>
    string[] HandoffIids { get; }
}

/// <summary>Serves the build's real <see cref="DefaultTermIdentity"/> values.</summary>
public sealed class DefaultTermIdentityView : IDefaultTermIdentityView
{
    public static DefaultTermIdentityView Instance { get; } = new();

    public string TerminalClsid => DefaultTermIdentity.TerminalClsid;

    public string ConsoleClsid => DefaultTermIdentity.ConsoleClsid;

    public string ProxyClsid => DefaultTermIdentity.ProxyClsid;

    public string[] HandoffIids => DefaultTermIdentity.HandoffIids;
}

/// <summary>Outcome of an enable attempt.</summary>
public enum EnableResult
{
    /// <summary>The write set landed and the delegation pair is selected.</summary>
    Registered,

    /// <summary>The pair already pointed at this build; nothing was written.</summary>
    AlreadyRegistered,
}

/// <summary>Outcome of a disable attempt.</summary>
public enum DisableResult
{
    /// <summary>This build's selection was removed.</summary>
    Cleared,

    /// <summary>The delegation pair was not this build's; nothing was touched.</summary>
    NotRegistered,
}

/// <summary>
/// Reads and writes this build's default-terminal registration under an
/// injected registry root: <see cref="DefaultTermRegistryPaths.Production"/>
/// for the real per-user locations, <see cref="DefaultTermRegistryPaths.UnderTestRoot"/>
/// for a throwaway root nothing outside it can see.
///
/// Enable writes the handler classes and the proxy-stub interface entries
/// first and selects the Console delegation pair last, so a reader never
/// sees the selection pointing at bindings that are not registered yet.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefaultTermRegistration
{
    public const string DelegationConsoleValue = "DelegationConsole";
    public const string DelegationTerminalValue = "DelegationTerminal";
    public const string ThreadingModelValue = "ThreadingModel";

    private const string LocalServer32Subkey = "LocalServer32";
    private const string InprocServer32Subkey = "InprocServer32";
    private const string ProxyStubClsid32Subkey = "ProxyStubClsid32";

    private readonly DefaultTermRegistryPaths _paths;
    private readonly IDefaultTermIdentityView _identity;
    private readonly string _winttyExePath;
    private readonly string _openConsoleExePath;
    private readonly string _proxyDllPath;
    private readonly string[] _handoffIids;

    /// <summary>
    /// Receives each subkey path immediately before the engine writes it,
    /// in write order. Set it to audit the sequence; the selection key
    /// arriving last is the invariant the enable path holds.
    /// </summary>
    public Action<string>? WriteObserver { get; set; }

    public DefaultTermRegistration(
        DefaultTermRegistryPaths paths,
        IDefaultTermIdentityView identity,
        string winttyExePath,
        string openConsoleExePath,
        string proxyDllPath)
    {
        _paths = paths;
        _identity = identity;
        _winttyExePath = Path.GetFullPath(winttyExePath);
        _openConsoleExePath = Path.GetFullPath(openConsoleExePath);
        _proxyDllPath = Path.GetFullPath(proxyDllPath);
        // The view's array is exposed; the engine keeps its own copy and
        // never mutates or aliases the caller's.
        _handoffIids = (string[])identity.HandoffIids.Clone();
    }

    /// <summary>
    /// True when the Console delegation pair names this build's console
    /// and terminal CLSIDs, and nothing else.
    /// </summary>
    public bool IsRegistered()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var startup = baseKey.OpenSubKey(_paths.StartupSubkey);
        if (startup is null)
        {
            return false;
        }

        return Matches(startup.GetValue(DelegationConsoleValue), _identity.ConsoleClsid)
            && Matches(startup.GetValue(DelegationTerminalValue), _identity.TerminalClsid);
    }

    /// <summary>
    /// Writes the exact registration write set: the terminal and console
    /// handler classes, the proxy class, one proxy-stub entry per handoff
    /// interface, and the Console delegation pair, in that order.
    /// </summary>
    public EnableResult Enable()
    {
        if (IsRegistered())
        {
            return EnableResult.AlreadyRegistered;
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

        Write(baseKey, _paths.ClsidSubkeyOf(_identity.TerminalClsid) + "\\" + LocalServer32Subkey,
            key => key.SetValue(null, Quote(_winttyExePath), RegistryValueKind.String));
        Write(baseKey, _paths.ClsidSubkeyOf(_identity.ConsoleClsid) + "\\" + LocalServer32Subkey,
            key => key.SetValue(null, Quote(_openConsoleExePath), RegistryValueKind.String));
        Write(baseKey, _paths.ClsidSubkeyOf(_identity.ProxyClsid) + "\\" + InprocServer32Subkey, key =>
        {
            // The COM loader takes this value raw: quotes would become part
            // of the path, so unlike the LocalServer32 entries this one
            // stays unquoted.
            key.SetValue(null, _proxyDllPath, RegistryValueKind.String);
            key.SetValue(ThreadingModelValue, "Both", RegistryValueKind.String);
        });
        foreach (var iid in _handoffIids)
        {
            Write(baseKey, _paths.InterfaceSubkeyOf(iid) + "\\" + ProxyStubClsid32Subkey,
                key => key.SetValue(null, _identity.ProxyClsid, RegistryValueKind.String));
        }

        // Last, deliberately: the selection is what makes the chain live,
        // and it must never be readable before the chain it names exists.
        Write(baseKey, _paths.StartupSubkey, key =>
        {
            key.SetValue(DelegationConsoleValue, _identity.ConsoleClsid, RegistryValueKind.String);
            key.SetValue(DelegationTerminalValue, _identity.TerminalClsid, RegistryValueKind.String);
        });

        return EnableResult.Registered;
    }

    /// <summary>Removes this build's selection. Lands with the disable path.</summary>
    public DisableResult Disable() => throw new NotImplementedException();

    /// <summary>True while this build's selection survives a hostile reset. Lands with the ownership work.</summary>
    public bool OwnsSelection() => throw new NotImplementedException();

    private void Write(RegistryKey baseKey, string subkey, Action<RegistryKey> fill)
    {
        WriteObserver?.Invoke(subkey);
        using var key = baseKey.CreateSubKey(subkey);
        fill(key);
    }

    private bool Matches(object? value, string expectedClsid) =>
        string.Equals(value as string, expectedClsid, StringComparison.OrdinalIgnoreCase);

    private static string Quote(string path) => "\"" + path + "\"";
}
