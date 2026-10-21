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

/// <summary>Terminal state of an enable attempt.</summary>
public enum EnableStatus
{
    /// <summary>The write set landed and the delegation pair is selected.</summary>
    Registered,

    /// <summary>The pair already pointed at this build; nothing was written.</summary>
    AlreadyRegistered,

    /// <summary>The attempt failed and the registry was restored to its pre-attempt state.</summary>
    Failed,
}

/// <summary>Why an enable attempt failed.</summary>
public enum EnableFailureKind
{
    /// <summary>No failure; the attempt succeeded.</summary>
    None,

    /// <summary>A pre-existing target key could not be swept and recreated.</summary>
    UndeletableKey,

    /// <summary>A value write failed partway through the write set.</summary>
    WriteFailed,
}

/// <summary>
/// Outcome of an enable attempt. Value equality, so tests compare against
/// the static outcomes directly; <see cref="FailedKey"/> names the key an
/// <see cref="EnableStatus.Failed"/> attempt failed on.
/// </summary>
public sealed record EnableResult(
    EnableStatus Status,
    EnableFailureKind FailureKind = EnableFailureKind.None,
    string? FailedKey = null)
{
    public static EnableResult Registered { get; } = new(EnableStatus.Registered);

    public static EnableResult AlreadyRegistered { get; } = new(EnableStatus.AlreadyRegistered);

    public static EnableResult Failed(EnableFailureKind kind, string key) =>
        new(EnableStatus.Failed, kind, key);
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
/// Enable treats every key it targets as hostile until proven its own: each
/// target tree is snapshotted, deleted, and recreated clean before any value
/// is written, and any failure rolls the whole attempt back to the exact
/// pre-attempt state. A half-registration is never an observable outcome:
/// the bindings land before the selection pair, and the console value lands
/// before the terminal value inside it.
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
    /// Receives each subkey path immediately before the engine first writes
    /// a value into it, in write order. Set it to audit the sequence; the
    /// selection key arriving last is the invariant the enable path holds.
    /// </summary>
    public Action<string>? WriteObserver { get; set; }

    /// <summary>
    /// Receives each (subkey, value name) pair after the value lands.
    /// Value-level audit seam: the console value landing before the
    /// terminal value inside the selection key is observable only here.
    /// </summary>
    public Action<string, string?>? ValueWriteObserver { get; set; }

    /// <summary>
    /// Test seam: return an exception to make the sweep of that key path
    /// fail, simulating a key the user cannot delete. Null lets the sweep
    /// proceed.
    /// </summary>
    public Func<string, Exception?>? ResetFault { get; set; }

    /// <summary>
    /// Test seam: called before each value write with the subkey path and
    /// value name (null for the default). Return an exception to fail that
    /// write, simulating a mid-write-set failure.
    /// </summary>
    public Func<string, string?, Exception?>? ValueWriteFault { get; set; }

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
    /// interface, and the Console delegation pair, in that order. Every
    /// target key is swept clean first (a pre-seeded TreatAs, a hostile
    /// server command, an unexpected subkey: all gone before our values
    /// land), and any failure rolls the registry back to its pre-attempt
    /// state, so the attempt is atomic in effect.
    /// </summary>
    public EnableResult Enable()
    {
        if (IsRegistered())
        {
            return EnableResult.AlreadyRegistered;
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        var journal = new List<JournalEntry>();
        var observedSubkeys = new HashSet<string>(StringComparer.Ordinal);

        // Phase one: sweep every target tree. A key that exists is
        // snapshotted and recreated empty; a key that does not exist is
        // created and journaled for removal on rollback.
        string? failingTarget = null;
        try
        {
            foreach (var target in SweepTargets())
            {
                var sweepFault = ResetFault?.Invoke(target);
                if (sweepFault is not null)
                {
                    failingTarget = target;
                    break;
                }

                using var existing = baseKey.OpenSubKey(target);
                if (existing is null)
                {
                    baseKey.CreateSubKey(target).Dispose();
                    journal.Add(new CreatedTree(target));
                    continue;
                }

                var snapshot = SnapshotTree(baseKey, target);
                baseKey.DeleteSubKeyTree(target, throwOnMissingSubKey: false);
                baseKey.CreateSubKey(target).Dispose();
                journal.Add(new ReplacedTree(target, snapshot));
            }

            if (failingTarget is not null)
            {
                Rollback(baseKey, journal);
                return EnableResult.Failed(EnableFailureKind.UndeletableKey, failingTarget);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Rollback(baseKey, journal);
            return EnableResult.Failed(
                EnableFailureKind.UndeletableKey,
                failingTarget ?? FailingTarget(journal) ?? "(unknown)");
        }

        // Phase two: write the values into the swept trees. Bindings
        // before the selection; inside the selection, console before
        // terminal. Every value write is journaled so a fault between the
        // two selection values still rolls back to nothing.
        try
        {
            SetValue(baseKey, journal, observedSubkeys,
                _paths.ClsidSubkeyOf(_identity.TerminalClsid) + "\\" + LocalServer32Subkey,
                null, Quote(_winttyExePath), RegistryValueKind.String);
            SetValue(baseKey, journal, observedSubkeys,
                _paths.ClsidSubkeyOf(_identity.ConsoleClsid) + "\\" + LocalServer32Subkey,
                null, Quote(_openConsoleExePath), RegistryValueKind.String);
            SetValue(baseKey, journal, observedSubkeys,
                _paths.ClsidSubkeyOf(_identity.ProxyClsid) + "\\" + InprocServer32Subkey,
                null, _proxyDllPath, RegistryValueKind.String);
            SetValue(baseKey, journal, observedSubkeys,
                _paths.ClsidSubkeyOf(_identity.ProxyClsid) + "\\" + InprocServer32Subkey,
                ThreadingModelValue, "Both", RegistryValueKind.String);
            foreach (var iid in _handoffIids)
            {
                SetValue(baseKey, journal, observedSubkeys,
                    _paths.InterfaceSubkeyOf(iid) + "\\" + ProxyStubClsid32Subkey,
                    null, _identity.ProxyClsid, RegistryValueKind.String);
            }

            // Last, deliberately: the selection is what makes the chain live,
            // and it must never be readable before the chain it names exists.
            SetValue(baseKey, journal, observedSubkeys,
                _paths.StartupSubkey,
                DelegationConsoleValue, _identity.ConsoleClsid, RegistryValueKind.String);
            SetValue(baseKey, journal, observedSubkeys,
                _paths.StartupSubkey,
                DelegationTerminalValue, _identity.TerminalClsid, RegistryValueKind.String);
        }
        catch (ValueWriteFaultException e)
        {
            Rollback(baseKey, journal);
            return EnableResult.Failed(EnableFailureKind.WriteFailed, e.Target);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Rollback(baseKey, journal);
            return EnableResult.Failed(EnableFailureKind.WriteFailed, "(unknown)");
        }

        return EnableResult.Registered;
    }

    /// <summary>Removes this build's selection. Lands with the disable path.</summary>
    public DisableResult Disable() => throw new NotImplementedException();

    /// <summary>True while this build's selection survives a hostile reset. Lands with the ownership work.</summary>
    public bool OwnsSelection() => throw new NotImplementedException();

    private string[] SweepTargets()
    {
        var targets = new List<string>
        {
            _paths.ClsidSubkeyOf(_identity.TerminalClsid),
            _paths.ClsidSubkeyOf(_identity.ConsoleClsid),
            _paths.ClsidSubkeyOf(_identity.ProxyClsid),
        };
        targets.AddRange(_handoffIids.Select(_paths.InterfaceSubkeyOf));
        targets.Add(_paths.StartupSubkey);
        return targets.ToArray();
    }

    private void SetValue(
        RegistryKey baseKey,
        List<JournalEntry> journal,
        HashSet<string> observedSubkeys,
        string subkey,
        string? name,
        object value,
        RegistryValueKind kind)
    {
        if (observedSubkeys.Add(subkey))
        {
            WriteObserver?.Invoke(subkey);
        }

        var fault = ValueWriteFault?.Invoke(subkey, name);
        if (fault is not null)
        {
            throw new ValueWriteFaultException(subkey, name, fault);
        }

        using var key = baseKey.OpenSubKey(subkey, writable: true)
            ?? baseKey.CreateSubKey(subkey);
        var oldValue = key.GetValue(name);
        var oldKind = oldValue is null ? RegistryValueKind.None : key.GetValueKind(name);
        journal.Add(new ValueWritten(subkey, name, oldValue, oldKind, oldValue is not null));
        key.SetValue(name, value, kind);
        ValueWriteObserver?.Invoke(subkey, name);
    }

    private static List<KeyEntry> SnapshotTree(RegistryKey baseKey, string path)
    {
        var entries = new List<KeyEntry>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            using var key = baseKey.OpenSubKey(current);
            if (key is null)
            {
                continue;
            }

            var relative = current == path ? string.Empty : current[(path.Length + 1)..];
            foreach (var name in key.GetValueNames())
            {
                entries.Add(new KeyEntry(relative, name, key.GetValue(name), key.GetValueKind(name)));
            }

            foreach (var child in key.GetSubKeyNames())
            {
                pending.Push(current + "\\" + child);
            }
        }

        return entries;
    }

    private static void Rollback(RegistryKey baseKey, List<JournalEntry> journal)
    {
        for (var i = journal.Count - 1; i >= 0; i--)
        {
            switch (journal[i])
            {
                case ValueWritten written:
                    using (var key = baseKey.OpenSubKey(written.Subkey, writable: true))
                    {
                        if (key is null)
                        {
                            continue;
                        }

                        if (written.Existed)
                        {
                            key.SetValue(written.Name, written.OldValue!, written.OldKind);
                        }
                        else if (written.Name is not null)
                        {
                            key.DeleteValue(written.Name, throwOnMissingValue: false);
                        }
                        else
                        {
                            key.SetValue(null, string.Empty, RegistryValueKind.String);
                        }
                    }

                    break;

                case ReplacedTree replaced:
                    baseKey.DeleteSubKeyTree(replaced.Path, throwOnMissingSubKey: false);
                    if (replaced.Entries.Count == 0)
                    {
                        baseKey.CreateSubKey(replaced.Path).Dispose();
                        break;
                    }

                    foreach (var entry in replaced.Entries)
                    {
                        var full = entry.RelativePath.Length == 0
                            ? replaced.Path
                            : replaced.Path + "\\" + entry.RelativePath;
                        using var key = baseKey.CreateSubKey(full);
                        key.SetValue(entry.Name, entry.Value!, entry.Kind);
                    }

                    break;

                case CreatedTree created:
                    baseKey.DeleteSubKeyTree(created.Path, throwOnMissingSubKey: false);
                    break;
            }
        }
    }

    private static string? FailingTarget(List<JournalEntry> journal)
    {
        if (journal.Count == 0)
        {
            return null;
        }

        return journal[^1] switch
        {
            ReplacedTree replaced => replaced.Path,
            CreatedTree created => created.Path,
            _ => null,
        };
    }

    private bool Matches(object? value, string expectedClsid) =>
        string.Equals(value as string, expectedClsid, StringComparison.OrdinalIgnoreCase);

    private static string Quote(string path) => "\"" + path + "\"";

    private abstract record JournalEntry;

    private sealed record CreatedTree(string Path) : JournalEntry;

    private sealed record ReplacedTree(string Path, List<KeyEntry> Entries) : JournalEntry;

    private sealed record ValueWritten(
        string Subkey,
        string? Name,
        object? OldValue,
        RegistryValueKind OldKind,
        bool Existed) : JournalEntry;

    private sealed record KeyEntry(string RelativePath, string? Name, object? Value, RegistryValueKind Kind);

    private sealed class ValueWriteFaultException : Exception
    {
        public ValueWriteFaultException(string subkey, string? name, Exception inner)
            : base($"injected write fault at {subkey}:{name ?? "(default)"}", inner)
        {
            Target = subkey + ":" + (name ?? "(default)");
        }

        public string Target { get; }
    }
}
