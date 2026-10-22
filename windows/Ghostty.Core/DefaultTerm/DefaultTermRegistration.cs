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
public sealed class DefaultTermRegistration : IDefaultTermEngine
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
    private readonly DefaultTermCaptureStore? _store;
    private readonly string? _storePath;

    /// <summary>
    /// A nil terminal or console CLSID means this build never received
    /// real identity: inert by construction, never a registrant.
    /// </summary>
    private bool IdentityIsPlaceholder() =>
        _identity.TerminalClsid.Contains("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase)
        || _identity.ConsoleClsid.Contains("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase);

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
        : this(paths, identity, winttyExePath, openConsoleExePath, proxyDllPath, storePath: null)
    {
    }

    public DefaultTermRegistration(
        DefaultTermRegistryPaths paths,
        IDefaultTermIdentityView identity,
        string winttyExePath,
        string openConsoleExePath,
        string proxyDllPath,
        string? storePath)
    {
        _paths = paths;
        _identity = identity;
        _winttyExePath = Path.GetFullPath(winttyExePath);
        _openConsoleExePath = Path.GetFullPath(openConsoleExePath);
        _proxyDllPath = Path.GetFullPath(proxyDllPath);
        // The view's array is exposed; the engine keeps its own copy and
        // never mutates or aliases the caller's.
        _handoffIids = (string[])identity.HandoffIids.Clone();
        _storePath = storePath;
        _store = storePath is null ? null : new DefaultTermCaptureStore(storePath);
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
        if (IdentityIsPlaceholder())
        {
            return EnableResult.Failed(
                EnableFailureKind.None,
                "identity is placeholder: this build carries nil CLSIDs and registers nothing");
        }

        if (IsRegistered())
        {
            return EnableResult.AlreadyRegistered;
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        // The store write belongs to the enable transaction: a capture
        // saved here and then left behind by a failed write would make a
        // LATER disable restore a prior that never actually existed,
        // possibly re-selecting the edition being disabled. The
        // previous store content goes back on failure.
        byte[]? previousStore = null;
        var hadStore = false;
        if (_store is not null && File.Exists(_storePath))
        {
            try
            {
                previousStore = File.ReadAllBytes(_storePath);
                hadStore = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable previous store: proceed; the compensation
                // below then just deletes whatever this enable wrote.
            }
        }

        _store?.Save(CapturePrior(baseKey));
        var result = WriteRegistration(baseKey);
        if (result.Status == EnableStatus.Failed && _store is not null)
        {
            try
            {
                if (hadStore && previousStore is not null)
                {
                    File.WriteAllBytes(_storePath, previousStore);
                }
                else
                {
                    File.Delete(_storePath);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a stale store is the nil-store degradation
                // at worst, never a wrong selection.
            }
        }

        return result;
    }

    /// <summary>
    /// Rewrites the whole registration without capturing priors: the
    /// repair path for an enabled build whose install location moved.
    /// Assumes the caller verified the selection is still ours.
    /// </summary>
    public void RepairBindings()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        WriteRegistration(baseKey);
    }

    /// <summary>True while the shared console binding names this build's binary.</summary>
    public bool ConsoleBindingIsOurs()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        return GroupNamesOurBinary(baseKey);
    }

    private EnableResult WriteRegistration(RegistryKey baseKey)
    {
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

    /// <summary>
    /// Removes this build's registration. Own-outright keys (the terminal
    /// class and the capture store) always go. The shared group goes or is
    /// restored only while its console binding still names this build's
    /// binary; the selection is rewritten only when this build owns it,
    /// to the captured prior when its binary is live and to the nil pair
    /// otherwise. A non-owner never moves anything it did not write.
    /// </summary>
    public DisableResult Disable()
    {
        if (IdentityIsPlaceholder())
        {
            // Nothing this build ever wrote is in the tree; refusing
            // keeps a nil-identity build from touching shared keys.
            return DisableResult.NotRegistered;
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

        // The prior is read before the store is deleted: the captured
        // state drives the restore decisions below.
        var prior = _store?.Load() ?? CapturedState.Nil;

        // Step one, unconditional: this build's own-outright artifacts.
        baseKey.DeleteSubKeyTree(_paths.ClsidSubkeyOf(_identity.TerminalClsid), throwOnMissingSubKey: false);
        _store?.Delete();

        var (selectionConsole, selectionTerminal) = ReadSelection(baseKey);
        var ownsSelection =
            Matches(selectionConsole, _identity.ConsoleClsid)
            && Matches(selectionTerminal, _identity.TerminalClsid);
        var groupOwned = GroupNamesOurBinary(baseKey);

        if (!ownsSelection)
        {
            // Shadow-unwrite: the selection moved on, but a console
            // binding this build wrote may still intercept its traffic.
            if (groupOwned)
            {
                if (CorrespondsToPrior(prior, selectionTerminal) && PriorBinaryLive(prior))
                {
                    RestoreGroup(baseKey, prior);
                }
                else
                {
                    RemoveGroup(baseKey);
                }
            }

            return DisableResult.NotRegistered;
        }

        // Owner: rewrite the selection first, then settle the group.
        if (prior.HasSelectionPrior && PriorBinaryLive(prior))
        {
            using (var startup = baseKey.CreateSubKey(_paths.StartupSubkey))
            {
                startup.SetValue(DelegationConsoleValue, prior.PriorConsoleValue!, RegistryValueKind.String);
                startup.SetValue(DelegationTerminalValue, prior.PriorTerminalValue!, RegistryValueKind.String);
            }
        }
        else
        {
            using (var startup = baseKey.CreateSubKey(_paths.StartupSubkey))
            {
                startup.SetValue(DelegationConsoleValue, NilGuid, RegistryValueKind.String);
                startup.SetValue(DelegationTerminalValue, NilGuid, RegistryValueKind.String);
            }
        }

        if (groupOwned)
        {
            if (prior.SharedGroupValues.Count > 0)
            {
                RestoreGroup(baseKey, prior);
            }
            else
            {
                RemoveGroup(baseKey);
            }
        }

        return DisableResult.Cleared;
    }

    /// <summary>True while the selection pair names this build's pair.</summary>
    public bool OwnsSelection()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        var (selectionConsole, selectionTerminal) = ReadSelection(baseKey);
        return Matches(selectionConsole, _identity.ConsoleClsid)
            && Matches(selectionTerminal, _identity.TerminalClsid);
    }

    private const string NilGuid = "{00000000-0000-0000-0000-000000000000}";

    private CapturedState CapturePrior(RegistryKey baseKey)
    {
        var (priorConsole, priorTerminal) = ReadSelection(baseKey);
        var hasSelectionPrior = priorConsole is not null || priorTerminal is not null;

        // Whole subtrees, not one value-set per root: the sweep deletes
        // these trees whole, so a capture narrower than the sweep
        // "restores" a mutilated prior. Every string, dword and qword
        // value in each tree is captured with its kind.
        var shared = new List<CapturedSharedValue>();
        var roots = new List<string>
        {
            _paths.ClsidSubkeyOf(_identity.ConsoleClsid),
            _paths.ClsidSubkeyOf(_identity.ProxyClsid),
        };
        roots.AddRange(_handoffIids.Select(_paths.InterfaceSubkeyOf));
        foreach (var root in roots)
        {
            foreach (var entry in SnapshotTree(baseKey, root))
            {
                var text = CaptureValueString(entry.Value, entry.Kind);
                if (text is null)
                {
                    continue;
                }

                var subkey = entry.RelativePath.Length == 0
                    ? root
                    : root + "\\" + entry.RelativePath;
                shared.Add(new CapturedSharedValue(subkey, entry.Name, text, entry.Kind));
            }
        }

        var consoleLocalServer = _paths.ClsidSubkeyOf(_identity.ConsoleClsid)
            + "\\" + LocalServer32Subkey;
        string? priorBinary = null;
        foreach (var entry in shared.Where(v =>
                     v.Subkey == consoleLocalServer && string.IsNullOrEmpty(v.Name)))
        {
            priorBinary = Unquote(entry.Value);
        }

        return new CapturedState(
            hasSelectionPrior,
            priorConsole,
            priorTerminal,
            priorBinary,
            shared,
            DateTimeOffset.UtcNow);
    }

    private static string? CaptureValueString(object? value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => value as string,
        RegistryValueKind.DWord when value is int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RegistryValueKind.QWord when value is long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // Binary and multi-string kinds are not carried: the sweep still
        // removes them, the restore cannot rebuild them.
        _ => null,
    };

    private static object ParseCapturedValue(string value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => value,
    };

    private (string? Console, string? Terminal) ReadSelection(RegistryKey baseKey)
    {
        using var startup = baseKey.OpenSubKey(_paths.StartupSubkey);
        if (startup is null)
        {
            return (null, null);
        }

        return (
            startup.GetValue(DelegationConsoleValue) as string,
            startup.GetValue(DelegationTerminalValue) as string);
    }

    private bool GroupNamesOurBinary(RegistryKey baseKey)
    {
        using var server = baseKey.OpenSubKey(
            _paths.ClsidSubkeyOf(_identity.ConsoleClsid) + "\\" + LocalServer32Subkey);
        if (server?.GetValue(null) is not string command)
        {
            return false;
        }

        return string.Equals(
            NormalizeCommand(command),
            NormalizeCommand(_openConsoleExePath),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One normalization for every command-path comparison: quotes
    /// stripped and environment strings expanded, so a foreign-format
    /// value naming the same binary classifies the same everywhere.
    /// </summary>
    private static string NormalizeCommand(string value) =>
        Environment.ExpandEnvironmentVariables(Unquote(value));

    private static bool CorrespondsToPrior(CapturedState prior, string? selectionTerminal) =>
        prior.HasSelectionPrior
        && selectionTerminal is not null
        && string.Equals(selectionTerminal, prior.PriorTerminalValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A restored prior applies only when it still names binaries that
    /// exist: the console half by its captured path, the terminal half
    /// by resolving the prior terminal CLSID's registration at restore
    /// time. A dead half falls back to the nil pair rather than
    /// restoring a selection that degrades every launch.
    /// </summary>
    private bool PriorBinaryLive(CapturedState prior)
    {
        if (prior.PriorConsoleBinary is not null && !File.Exists(prior.PriorConsoleBinary))
        {
            return false;
        }

        if (prior.PriorTerminalValue is null)
        {
            return true;
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var server = baseKey.OpenSubKey(
            _paths.ClsidSubkeyOf(prior.PriorTerminalValue) + "\\" + LocalServer32Subkey);
        return server?.GetValue(null) is string command
            && File.Exists(NormalizeCommand(command));
    }

    private void RemoveGroup(RegistryKey baseKey)
    {
        // The console CLSID key is this edition's own. The proxy CLSID
        // key and the three Interface keys are edition-invariant real
        // estate every classic defterm implementation registers too, so
        // each is removed only while its current value still names OUR
        // proxy: a foreign owner's registration survives our disable.
        baseKey.DeleteSubKeyTree(_paths.ClsidSubkeyOf(_identity.ConsoleClsid), throwOnMissingSubKey: false);
        if (ProxyKeyIsOurs(baseKey))
        {
            baseKey.DeleteSubKeyTree(_paths.ClsidSubkeyOf(_identity.ProxyClsid), throwOnMissingSubKey: false);
        }

        foreach (var iid in _handoffIids)
        {
            if (InterfaceKeyIsOurs(baseKey, iid))
            {
                baseKey.DeleteSubKeyTree(_paths.InterfaceSubkeyOf(iid), throwOnMissingSubKey: false);
            }
        }
    }

    private bool ProxyKeyIsOurs(RegistryKey baseKey)
    {
        using var key = baseKey.OpenSubKey(
            _paths.ClsidSubkeyOf(_identity.ProxyClsid) + "\\" + InprocServer32Subkey);
        return key?.GetValue(null) is string dll
            && string.Equals(
                NormalizeCommand(dll),
                NormalizeCommand(_proxyDllPath),
                StringComparison.OrdinalIgnoreCase);
    }

    private bool InterfaceKeyIsOurs(RegistryKey baseKey, string iid)
    {
        using var key = baseKey.OpenSubKey(
            _paths.InterfaceSubkeyOf(iid) + "\\" + ProxyStubClsid32Subkey);
        return key?.GetValue(null) is string clsid
            && string.Equals(clsid.Trim(), _identity.ProxyClsid, StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreGroup(RegistryKey baseKey, CapturedState prior)
    {
        foreach (var value in prior.SharedGroupValues)
        {
            using var key = baseKey.CreateSubKey(value.Subkey);
            key.SetValue(value.Name, ParseCapturedValue(value.Value, value.Kind), value.Kind);
        }
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"')
            ? trimmed[1..^1]
            : trimmed;
    }

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
