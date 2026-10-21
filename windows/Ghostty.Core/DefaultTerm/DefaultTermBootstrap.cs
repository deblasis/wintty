using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Ghostty.Core.SingleInstance;

namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// The process token's integrity, reduced to the classes the
/// registration rules discriminate on.
/// </summary>
public enum TokenIntegrity
{
    Low,
    Medium,
    High,
}

/// <summary>
/// The inputs of the registration decision. The two machine-dependent
/// checks are delegates so a test never reads a real token or the real
/// registry: production builds them with
/// <see cref="DefaultTermBootstrap.BuildContext"/>.
/// </summary>
public sealed record BootstrapContext(
    SingleInstanceRole Role,
    bool FeatureEnabled,
    bool CanRegister,
    Func<TokenIntegrity> IntegrityLevel,
    Func<bool> BoundPathCheck);

/// <summary>
/// Whether and how this instance may expose the handoff class object,
/// per the design's receiver-lifecycle rules.
/// </summary>
public static partial class DefaultTermBootstrap
{
    /// <summary>
    /// The explicit dev escape from the bound-path self-check, for
    /// materialized build trees and checkout runs. Nothing in any
    /// recipe, smoke or CI run sets it.
    /// </summary>
    public const string UnboundEnvVar = "WINTTY_DEFTERM_UNBOUND";

    /// <summary>
    /// Permanent registration: the instance won the single-instance
    /// election, has the feature enabled, is a registrable edition,
    /// passes the bound-path self-check, and is not elevated (medium
    /// integrity, own token). Those are the design's four conditions,
    /// cheapest first.
    /// </summary>
    public static bool ShouldRegister(BootstrapContext ctx) =>
        ctx.Role == SingleInstanceRole.Primary
        && ctx.FeatureEnabled
        && ctx.CanRegister
        && ctx.BoundPathCheck()
        && ctx.IntegrityLevel() == TokenIntegrity.Medium;

    /// <summary>
    /// Transient registration: enabled plus the self-check. No election
    /// requirement, elevated allowed: an SCM-launched instance serves
    /// exactly its own activation and never outlives it.
    /// </summary>
    public static bool ShouldRegisterTransient(BootstrapContext ctx) =>
        ctx.FeatureEnabled
        && ctx.CanRegister
        && ctx.BoundPathCheck();

    /// <summary>
    /// COM's activation launch spells the flag both ways; matching is
    /// case-insensitive like every other argument comparison in the
    /// process.
    /// </summary>
    public static bool IsEmbeddingLaunch(string[] args) =>
        args.Any(a => a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("/Embedding", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The production context: integrity from this process's own token,
    /// bound path against the real per-user registration.
    /// </summary>
    public static BootstrapContext BuildContext(
        SingleInstanceRole role,
        bool featureEnabled) => new(
        role,
        featureEnabled,
        DefaultTermIdentity.CanRegister,
        ReadIntegrityLevel,
        PassesBoundPathCheck);

    /// <summary>
    /// The production engine bound to this process's install layout:
    /// OpenConsole.exe and OpenConsoleProxy.dll sit beside this exe.
    /// </summary>
    /// <summary>
    /// The capture store's path, under the user's local app data so the
    /// engine can restore a captured prior after an install move. The
    /// subfolder follows the per-edition state directory channel when
    /// one is published; dev builds share the generic folder.
    /// </summary>
    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wintty",
        "defterm-state.json");

    public static DefaultTermRegistration CreateEngine() => new(
        DefaultTermRegistryPaths.Production,
        DefaultTermIdentityView.Instance,
        Environment.ProcessPath ?? string.Empty,
        Path.Combine(AppContext.BaseDirectory, "OpenConsole.exe"),
        Path.Combine(AppContext.BaseDirectory, "OpenConsoleProxy.dll"),
        DefaultStorePath);

    /// <summary>
    /// The feature is enabled when the selection names this build's own
    /// <summary>
    /// Whether this process runs above medium integrity. Elevated
    /// receivers serve their activation and never hold a class object.
    /// </summary>
    public static bool IsElevatedProcess =>
        ReadIntegrityLevel() != TokenIntegrity.Medium;

    /// <summary>
    /// The feature is enabled when the selection names this build's own
    /// pair: while it is disabled, nothing is exposed.
    /// </summary>
    public static bool IsFeatureEnabled() =>
        DefaultTermIdentity.CanRegister && CreateEngine().IsRegistered();

    /// <summary>
    /// The own-token integrity class. Anything the query cannot answer
    /// reports High: a build that cannot ask is not a build that may
    /// register, and the fail-closed direction is always the safe one.
    /// </summary>
    internal static unsafe TokenIntegrity ReadIntegrityLevel()
    {
        HANDLE token;
        if (!DWritePInvoke.OpenProcessToken(
                DWritePInvoke.GetCurrentProcess(),
                TOKEN_ACCESS_MASK.TOKEN_QUERY,
                &token))
        {
            return TokenIntegrity.High;
        }

        try
        {
            uint needed = 0;
            _ = DWritePInvoke.GetTokenInformation(
                token,
                TOKEN_INFORMATION_CLASS.TokenIntegrityLevel,
                null,
                0,
                &needed);
            if (needed == 0)
            {
                return TokenIntegrity.High;
            }

            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                uint written;
                if (!DWritePInvoke.GetTokenInformation(
                        token,
                        TOKEN_INFORMATION_CLASS.TokenIntegrityLevel,
                        p,
                        needed,
                        &written))
                {
                    return TokenIntegrity.High;
                }

                // TOKEN_MANDATORY_LABEL's first field is the SID pointer;
                // the integrity RID is that SID's last sub-authority.
                var sid = (IntPtr)p;
                var countPtr = GetSidSubAuthorityCount(sid);
                if (countPtr == IntPtr.Zero)
                {
                    return TokenIntegrity.High;
                }

                var count = Marshal.ReadByte(countPtr);
                if (count == 0)
                {
                    return TokenIntegrity.High;
                }

                var ridPtr = GetSidSubAuthority(sid, (uint)(count - 1));
                if (ridPtr == IntPtr.Zero)
                {
                    return TokenIntegrity.High;
                }

                const uint HighRid = 0x3000; // SECURITY_MANDATORY_HIGH_RID
                var rid = unchecked((uint)Marshal.ReadInt32(ridPtr));
                return rid >= HighRid ? TokenIntegrity.High : TokenIntegrity.Medium;
            }
        }
        finally
        {
            _ = DWritePInvoke.CloseHandle(token);
        }
    }

    /// <summary>
    /// This exe's fully-expanded path equals the normalized LocalServer32
    /// value registered for the terminal CLSID: quotes stripped,
    /// environment strings expanded.
    /// </summary>
    internal static bool PassesBoundPathCheck()
    {
        if (Environment.GetEnvironmentVariable(UnboundEnvVar) == "1")
        {
            return true;
        }

        var exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            return false;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            DefaultTermRegistryPaths.Production.ClsidSubkeyOf(
                DefaultTermIdentity.TerminalClsid)
            + @"\LocalServer32");
        var registered = key?.GetValue(null) as string;
        if (registered is null)
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(
            registered.Trim().Trim('"'));
        return string.Equals(
            Normalize(exePath),
            Normalize(expanded),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException or NotSupportedException)
        {
            return path;
        }
    }

    // Blittable SID helpers, hand-rolled rather than pulled through
    // CsWin32's PSID wrapper: the pointer arithmetic is on our side
    // either way.
    [LibraryImport("advapi32.dll")]
    private static partial IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
