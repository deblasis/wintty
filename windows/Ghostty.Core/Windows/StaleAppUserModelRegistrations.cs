using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Ghostty.Core.Windows;

/// <summary>
/// Removes the notification identities this app used to answer to and no longer does.
///
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;aumid&gt;</c> is created as a side effect of
/// <c>AppNotificationManager.Register()</c>, and nothing ever removes it. What is left behind is a
/// <c>CustomActivator</c> naming a COM class no running build claims, so the registration can
/// never produce a notification again and nothing will ever say so.
///
/// It does NOT clean up the row Windows shows in Settings &gt; System &gt; Notifications. That list is
/// driven by the notification platform's own database, which is a separate store: on the machine
/// this was found on, the key below was already gone while its row and its <c>NotificationHandler</c>
/// record both remained, and another identity had a record there with no key at all. Removing the
/// key stops it being read; it does not tidy the list.
/// </summary>
/// <remarks>
/// The whole risk here is deleting a key that something still uses, so the rule is that removal is
/// driven by <see cref="Superseded"/>, an explicit list, and never by the shape of a name. Matching
/// the AUMID namespace instead would take out a sibling flavour that happens to be installed:
/// Wintty ships several, they differ only by AUMID, and the one being removed would go quiet with
/// nothing to say why.
///
/// An entry earns its place on that list only by being impossible for a build that exists to use.
/// "Old" is not enough. <c>com.deblasis.wintty</c> and the tier names derived from it look
/// abandoned on a machine carrying the release AUMIDs, but they are the DEFAULT that
/// <c>_WinttyAumId</c> falls back to, so any public or untiered build registers under them, and
/// this must leave them alone.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StaleAppUserModelRegistrations
{
    private const string Root = @"Software\Classes\AppUserModelId";

    /// <summary>
    /// The identities that are provably dead, listed one by one.
    ///
    /// <c>com.deblasis.ghostty</c> is dead because the AUMID was a hardcoded literal for as long as
    /// it said "ghostty": the product was renamed to Wintty, the constant moved to
    /// <c>com.deblasis.wintty</c>, and the per-tier <c>_WinttyAumId</c> override that lets flavours
    /// differ arrived only after that move. So no build ever registered under
    /// <c>com.deblasis.ghostty</c> plus a tier suffix, and no build that can be produced today
    /// registers under the bare name either.
    /// </summary>
    public static readonly ImmutableArray<string> Superseded = ["com.deblasis.ghostty"];

    /// <summary>
    /// Remove the <see cref="Superseded"/> registrations. Returns how many keys were deleted.
    ///
    /// Two identities are held back rather than one. <paramref name="currentAumid"/> is what the
    /// caller set on the process, and <see cref="AppIdentity.AumId"/> is what this build was
    /// compiled with. Today they are the same constant and a wiring test keeps the one call site
    /// that way, so the second is redundant from here - it is there for the second call site, which
    /// would otherwise be able to delete the registration behind every notification this build
    /// sends by passing a string that is merely plausible.
    /// </summary>
    public static int RemoveSuperseded(string currentAumid) =>
        RemoveSuperseded(Superseded, [currentAumid, AppIdentity.AumId]);

    /// <summary>
    /// The same, over caller-supplied lists, so a test can exercise this against keys it owns.
    /// Nothing in <paramref name="live"/> is removed whatever <paramref name="superseded"/> says.
    ///
    /// Never throws. An orphaned row in a settings list is a cosmetic defect, and a registry hive
    /// can be locked down or redirected in ways this cannot anticipate; neither is worth a startup
    /// failure.
    /// </summary>
    public static int RemoveSuperseded(IReadOnlyList<string> superseded, IReadOnlyList<string> live)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(Root, writable: true);
            if (classes is null) return 0;

            var removed = 0;
            foreach (var aumid in superseded)
            {
                if (string.IsNullOrWhiteSpace(aumid)) continue;

                // A separator is not whitespace, and a name made only of them fixes up to the
                // empty string - at which point DeleteSubKeyTree deletes the key the handle is
                // open on, which here is the whole AppUserModelId hive: every desktop app on the
                // machine loses its notification identity. The existence probe below does not
                // catch it either, because OpenSubKey(@"\") returns the parent. A legal AUMID
                // never contains one, so refuse the whole class rather than the one spelling.
                if (aumid.Contains('\\') || aumid.Contains('/')) continue;

                if (IsLive(aumid, live)) continue;

                // Per entry, so one key the user cannot write does not cost the rest of the list.
                try
                {
                    // Existence checked first because DeleteSubKeyTree cannot report the
                    // difference between having removed something and having found nothing, and
                    // the second launch onward will always be the second case.
                    bool present;
                    using (var existing = classes.OpenSubKey(aumid)) present = existing is not null;
                    if (!present) continue;

                    classes.DeleteSubKeyTree(aumid, throwOnMissingSubKey: false);
                    removed++;
                }
                catch { /* One key that cannot be opened or deleted; the rest of the list still can. */ }
            }

            return removed;
        }
        catch
        {
            // The hive can be locked down or redirected in ways this cannot anticipate, and a
            // dangling activator entry is not worth failing a launch over.
            return 0;
        }
    }

    /// <summary>
    /// Whether an entry names an identity that is in use, in which case it is left alone whatever
    /// the superseded list says. Compared case-insensitively, because registry key names are.
    /// </summary>
    private static bool IsLive(string aumid, IReadOnlyList<string> live)
    {
        foreach (var identity in live)
            if (string.Equals(aumid, identity, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
