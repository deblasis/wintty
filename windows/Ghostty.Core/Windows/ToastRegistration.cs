using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Ghostty.Core.Windows;

/// <summary>
/// The values a toast registration carries, as read off
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;aumid&gt;</c> and the COM class
/// its <c>CustomActivator</c> names.
/// </summary>
/// <param name="Exists">Whether the AUMID key is there at all.</param>
/// <param name="DisplayName">The name Windows shows above a toast.</param>
/// <param name="IconUri">The icon Windows shows on a toast.</param>
/// <param name="ActivatorClsid">
/// The <c>CustomActivator</c> value: the COM class a toast click activates.
/// </param>
/// <param name="ActivatorServer">
/// That class's <c>LocalServer32</c> default value: the command line Windows
/// runs to deliver the click. An executable, usually quoted, usually with
/// arguments after it.
/// </param>
internal readonly record struct ToastRegistrationValues(
    bool Exists,
    string? DisplayName,
    string? IconUri,
    string? ActivatorClsid,
    string? ActivatorServer);

/// <summary>
/// What a launch has to do to an existing registration before it is usable
/// again. See <see cref="ToastRegistration.Diagnose"/>.
/// </summary>
/// <param name="Recreate">
/// The activator cannot reach this process, so the whole key has to go and be
/// written again by <c>Register()</c>.
/// </param>
/// <param name="RewriteDisplayName">The shown name is not this build's.</param>
/// <param name="RewriteIconUri">The shown icon is not this build's.</param>
internal readonly record struct ToastRegistrationRepair(
    bool Recreate,
    bool RewriteDisplayName,
    bool RewriteIconUri)
{
    public bool Any => Recreate || RewriteDisplayName || RewriteIconUri;
}

/// <summary>
/// Keeps this build's toast registration pointing at this build.
///
/// <c>AppNotificationManager.Register()</c> writes
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;aumid&gt;</c> once and then
/// leaves it alone: on a machine that already has the key, a later launch
/// re-registers against values an EARLIER install wrote. The two that go
/// wrong are the ones nothing else ever corrects. The <c>CustomActivator</c>
/// CLSID's <c>LocalServer32</c> still names the exe that first registered, so
/// once that install is gone a toast click launches a path that is not there
/// (deblasis/wintty-release#656, and the click half of #317); and the
/// <c>DisplayName</c> still reads whatever that install called itself, so a
/// tier installed over another one keeps the other one's name over every
/// toast it sends.
///
/// Neither is visible from inside the process: <c>Show()</c> keeps working
/// against the stale registration, so the toasts appear and only the click
/// is dead.
/// </summary>
/// <remarks>
/// <see cref="Diagnose"/> is the whole decision and is pure, so the rules can
/// be tested without a registry. The two registry halves around it are
/// deliberately thin, and neither throws: a notification identity is not
/// worth failing a launch over, and a hive can be locked down or redirected
/// in ways this cannot anticipate.
/// </remarks>
internal static class ToastRegistration
{
    private const string AumidRoot = @"Software\Classes\AppUserModelId";
    private const string ClsidRoot = @"Software\Classes\CLSID";

    /// <summary>
    /// What has to change, given what is on the machine and what this build
    /// wants.
    ///
    /// A missing key is not a repair: <c>Register()</c> is what creates one,
    /// and reporting work to do before it has run would delete nothing and
    /// say so every launch of a clean machine.
    ///
    /// An empty <paramref name="wantDisplayName"/> or
    /// <paramref name="wantIconUri"/> means the caller has no opinion, and an
    /// empty <paramref name="currentExePath"/> means the process could not
    /// name its own image. Neither is grounds for touching anything: this
    /// deletes registry keys unattended, so every rule here is a positive
    /// disagreement between two known values, never an absence.
    /// </summary>
    public static ToastRegistrationRepair Diagnose(
        in ToastRegistrationValues observed,
        string? currentExePath,
        string? wantDisplayName,
        string? wantIconUri)
    {
        if (!observed.Exists) return default;

        // A key with no activator can never deliver a click either, and it is
        // the same repair: let Register() write the pair again.
        var recreate = !string.IsNullOrWhiteSpace(currentExePath)
            && (string.IsNullOrWhiteSpace(observed.ActivatorClsid)
                || !SameExecutable(ServerExecutable(observed.ActivatorServer), currentExePath));

        var rewriteName = !string.IsNullOrWhiteSpace(wantDisplayName)
            && !string.Equals(observed.DisplayName, wantDisplayName, StringComparison.Ordinal);

        var rewriteIcon = !string.IsNullOrWhiteSpace(wantIconUri)
            && !string.Equals(observed.IconUri, wantIconUri, StringComparison.OrdinalIgnoreCase);

        return new ToastRegistrationRepair(recreate, rewriteName, rewriteIcon);
    }

    /// <summary>
    /// The executable out of a <c>LocalServer32</c> command line.
    ///
    /// The platform writes the path quoted and follows it with the activation
    /// argument, so the first quoted run is the answer when there is one. An
    /// unquoted value is taken whole: a path with a space in it and no quotes
    /// is already broken, and guessing where it ends would invent a
    /// disagreement out of a value nothing can use anyway.
    /// </summary>
    public static string? ServerExecutable(string? localServer32)
    {
        if (string.IsNullOrWhiteSpace(localServer32)) return null;

        var command = localServer32.Trim();
        if (command[0] != '"') return command;

        var close = command.IndexOf('"', 1);
        return close > 1 ? command[1..close] : null;
    }

    /// <summary>
    /// Whether two paths name the same image. Case-insensitive and separator-
    /// insensitive, because Windows paths are both; no filesystem probe,
    /// because this has to stay a decision over the values in hand.
    /// </summary>
    public static bool SameExecutable(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// Read the registration for <paramref name="aumid"/>. Never throws; an
    /// unreadable hive reads as "no key", which asks for no repair.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ToastRegistrationValues Read(string aumid)
    {
        if (!IsPlainAumid(aumid)) return default;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{AumidRoot}\{aumid}");
            if (key is null) return default;

            var clsid = key.GetValue("CustomActivator") as string;
            return new ToastRegistrationValues(
                Exists: true,
                DisplayName: key.GetValue("DisplayName") as string,
                IconUri: key.GetValue("IconUri") as string,
                ActivatorClsid: clsid,
                ActivatorServer: ReadLocalServer(clsid));
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Remove the registration and the COM class it names, so the next
    /// <c>Register()</c> writes both against this process. Returns whether
    /// the AUMID key was removed. Never throws.
    /// </summary>
    /// <param name="refused">
    /// Why a half that should have gone did not, naming the key and the OS
    /// message, so a refusal can be logged instead of leaving the launch
    /// half-repaired in silence. Null when nothing was refused; both halves'
    /// refusals arrive together when both refuse. This API swallows by
    /// contract - a notification identity is not worth failing a launch over
    /// - which is exactly why the reason has to come back out.
    /// </param>
    /// <remarks>
    /// The activator's CLSID key goes with it. Left behind it is a COM class
    /// whose server is a path that does not exist, which is the half of the
    /// defect that outlives the AUMID key: <c>Register()</c> mints a fresh
    /// CLSID rather than reusing the one it finds, so the orphan is never
    /// visited again and never corrected.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static bool Remove(string aumid, string? activatorClsid, out string? refused)
    {
        refused = null;
        if (!IsPlainAumid(aumid))
        {
            refused = $"\"{aumid}\" is not a name a subkey operation can be aimed at";
            return false;
        }

        var removed = false;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(AumidRoot, writable: true);
            if (root is null)
            {
                refused = $@"HKCU\{AumidRoot} could not be opened for writing";
            }
            else
            {
                using (var existing = root.OpenSubKey(aumid)) removed = existing is not null;
                if (removed) root.DeleteSubKeyTree(aumid, throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex)
        {
            // A key this user cannot delete is not worth a failed launch, and
            // the CLSID below is still worth trying.
            refused = $@"HKCU\{AumidRoot}\{aumid}: {ex.Message}";
        }

        if (IsPlainAumid(activatorClsid))
        {
            try
            {
                using var classes = Registry.CurrentUser.OpenSubKey(ClsidRoot, writable: true);
                if (classes is null)
                {
                    refused = JoinRefusals(
                        refused, $@"HKCU\{ClsidRoot} could not be opened for writing");
                }
                else
                {
                    classes.DeleteSubKeyTree(activatorClsid!, throwOnMissingSubKey: false);
                }
            }
            catch (Exception ex)
            {
                // Same: an orphaned COM class is inert, not fatal.
                refused = JoinRefusals(
                    refused, $@"HKCU\{ClsidRoot}\{activatorClsid}: {ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>Both halves can refuse in one call; the caller gets one
    /// reason carrying both, not only the first.</summary>
    private static string JoinRefusals(string? first, string second) =>
        first is null ? second : first + "; " + second;

    /// <summary>
    /// Write this build's <c>DisplayName</c> and <c>IconUri</c> straight onto
    /// an existing registration. Returns whether anything was written; never
    /// throws.
    /// </summary>
    /// <remarks>
    /// A direct write rather than another delete-and-re-register, because
    /// <c>Register()</c> derives these two from the process image and would
    /// write the same values back. Deleting on a disagreement it is going to
    /// reproduce is a delete on every single launch, forever.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static bool Apply(string aumid, in ToastRegistrationRepair repair, string? displayName, string? iconUri)
    {
        if (!IsPlainAumid(aumid)) return false;
        if (!repair.RewriteDisplayName && !repair.RewriteIconUri) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{AumidRoot}\{aumid}", writable: true);
            if (key is null) return false;

            if (repair.RewriteDisplayName) key.SetValue("DisplayName", displayName!, RegistryValueKind.String);
            if (repair.RewriteIconUri) key.SetValue("IconUri", iconUri!, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadLocalServer(string? clsid)
    {
        if (!IsPlainAumid(clsid)) return null;

        try
        {
            using var server = Registry.CurrentUser.OpenSubKey($@"{ClsidRoot}\{clsid}\LocalServer32");
            return server?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a name is safe to hand to a subkey operation.
    ///
    /// The separator check is the same class of hazard
    /// <see cref="StaleAppUserModelRegistrations"/> refuses: a name made of
    /// separators fixes up to the empty string, and DeleteSubKeyTree on that
    /// deletes the key the handle is open on, which here is the whole
    /// AppUserModelId or CLSID hive. Neither a legal AUMID nor a CLSID ever
    /// contains one.
    /// </summary>
    private static bool IsPlainAumid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !name.Contains('\\') && !name.Contains('/');
}
