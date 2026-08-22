using System;
using System.IO;
using Microsoft.Win32;

namespace Ghostty.Core.Windows;

/// <summary>
/// Corrects the icon and the name behind this process's AUMID.
///
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;aumid&gt;</c> is this app's notification identity.
/// We do not create it deliberately: it is a side effect of
/// <c>AppNotificationManager.Register()</c>, which fills it in and leaves
///
/// <list type="bullet">
/// <item>an <c>IconUri</c> pointing at a 16x16 PNG it generates under
/// <c>%LOCALAPPDATA%\Microsoft\WindowsAppSDK</c>, sized for a toast payload,</item>
/// <item>a <c>DisplayName</c> derived from the exe basename, which comes out as the bare product
/// name for every flavour - so a machine with several installed shows the same name several
/// times, with nothing to say which is which.</item>
/// </list>
///
/// Nothing has to be generated to fix either. The deployed <c>Assets\wintty.ico</c> already
/// carries eight images up to 256x256, and it is rasterised per edition, so pointing at it also
/// stops every flavour sharing one mark - on this machine seven of the eight registered PNGs were
/// byte-identical.
/// </summary>
/// <remarks>
/// Scope, measured rather than assumed: the Start menu does NOT read this key. Pointing a
/// registration at a deliberately different icon and name, restarting Explorer, and searching
/// Start produced no change at all - Start takes both from the shortcut. So this corrects the
/// notification identity and makes no claim about Start.
///
/// Two ordering constraints, and both are why this is a call at a particular moment rather than
/// something an installer could do once.
///
/// It must run AFTER <c>Register()</c>. Register rewrites <c>IconUri</c> on every launch, not only
/// on the first, so a value written before it - or written once at install time - is silently
/// reverted the next time the app starts. Re-applying on each launch is the point.
///
/// It must run after the process AUMID is set, because the key it corrects is the one
/// <c>Register()</c> chose from that identity. Passing the AUMID in rather than reading it back
/// keeps this honest: it writes to the identity the caller names, so it cannot quietly correct
/// some other flavour's key.
/// </remarks>
internal static class AppUserModelRegistration
{
    /// <summary>The icon deployed beside the exe, relative to the app directory.</summary>
    private const string IconRelativePath = @"Assets\wintty.ico";

    /// <summary>
    /// The multi-resolution icon deployed beside the running exe. Per-edition: the build
    /// rasterises this one, so a Pro install points at the Pro mark.
    /// </summary>
    public static string DeployedIconPath =>
        Path.Combine(AppContext.BaseDirectory, IconRelativePath);

    /// <summary>
    /// Point <paramref name="aumid"/>'s registration at <paramref name="iconPath"/> and at
    /// <paramref name="displayName"/>.
    ///
    /// Returns whether anything was written. Never throws: a wrong icon in All apps is a cosmetic
    /// defect, and taking startup down over it would be a much worse one.
    /// </summary>
    public static bool Apply(string aumid, string displayName, string iconPath)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;

        try
        {
            // Subkey rather than an interpolated path: an AUMID containing a backslash would
            // otherwise silently write one level deeper, under a key nothing reads.
            using var classes = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\AppUserModelId", writable: true);
            if (classes is null) return false;

            // Open rather than create. The key is Register()'s to make; if it is absent then
            // registration did not happen, and writing an icon into a key with no CustomActivator
            // would leave a registration that looks complete and activates nothing.
            using var key = classes.OpenSubKey(aumid, writable: true);
            if (key is null) return false;

            // Only when it is really there. Writing a path that does not resolve is worse than
            // leaving the toast PNG: Start would have nothing to fall back to.
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                key.SetValue("IconUri", iconPath, RegistryValueKind.String);

            if (!string.IsNullOrWhiteSpace(displayName))
                key.SetValue("DisplayName", displayName, RegistryValueKind.String);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
