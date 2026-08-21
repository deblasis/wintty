using System;
using Ghostty.Core;
using Ghostty.Core.Windows;
using Microsoft.Win32;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Deleting from the real registry is the behaviour, so these use it, under AUMIDs unique to the
/// run and removed again afterwards.
///
/// No test here names a real identity to the remover, and that is deliberate rather than tidiness:
/// the shipped <see cref="StaleAppUserModelRegistrations.Superseded"/> list, and the AUMIDs the
/// installed flavours answer to, are keys on the machine running the tests. A test run must not be
/// the thing that cleans a developer's shell up, and a mutation of the code under test must not be
/// able to make it one. What the shipped list contains is asserted on the list itself, without
/// going near a key.
/// </summary>
public sealed class StaleAppUserModelRegistrationsTests : IDisposable
{
    private const string Root = @"Software\Classes\AppUserModelId";

    private readonly string _orphan = "Ghostty.Tests.Orphan." + Guid.NewGuid().ToString("N");
    private readonly string _keeper = "Ghostty.Tests.Keeper." + Guid.NewGuid().ToString("N");
    private readonly string _current = "Ghostty.Tests.Current." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(Root, writable: true);
            if (classes is null) return;

            classes.DeleteSubKeyTree(_orphan, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(_keeper, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(_current, throwOnMissingSubKey: false);
        }
        catch (Exception) { }
    }

    /// <summary>What Register() leaves behind, which is what has to be removed.</summary>
    private static void CreateRegistration(string aumid)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(Root, writable: true)!;
        using var key = classes.CreateSubKey(aumid, writable: true)!;
        key.SetValue(
            "CustomActivator", "{" + Guid.NewGuid().ToString("D") + "}", RegistryValueKind.String);
        key.SetValue("DisplayName", "Wintty", RegistryValueKind.String);
    }

    private static bool Exists(string aumid)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{aumid}");
        return key is not null;
    }

    [Fact]
    public void ItRemovesAListedRegistration()
    {
        CreateRegistration(_orphan);

        Assert.Equal(1, StaleAppUserModelRegistrations.RemoveSuperseded([_orphan], [_current]));
        Assert.False(Exists(_orphan));
    }

    /// <summary>
    /// The listed name is the only thing that decides. A key that merely looks like one of ours -
    /// same namespace, same product, one suffix apart - is a sibling flavour's live notification
    /// identity, and removing it would silence an app the user did not touch.
    /// </summary>
    [Fact]
    public void ItLeavesAnUnlistedRegistrationAlone()
    {
        CreateRegistration(_orphan);
        CreateRegistration(_keeper);

        Assert.Equal(1, StaleAppUserModelRegistrations.RemoveSuperseded([_orphan], [_current]));
        Assert.True(Exists(_keeper));
    }

    /// <summary>
    /// The live identity wins over the list. That guard, not the list, is what protects the running
    /// process: a list that named this identity would otherwise delete the registration behind
    /// every toast this build sends, and the app would go quiet with the key gone until the next
    /// launch recreated it.
    /// </summary>
    [Fact]
    public void ItRefusesToRemoveALiveIdentity()
    {
        CreateRegistration(_current);

        Assert.Equal(0, StaleAppUserModelRegistrations.RemoveSuperseded([_current], [_current]));
        Assert.True(Exists(_current));
    }

    /// <summary>
    /// Every live identity, not just the first. The shipped call names two - the AUMID the process
    /// was given and the one the build was compiled with - so a guard that stopped at one would
    /// leave the other unprotected.
    /// </summary>
    [Fact]
    public void ItRefusesToRemoveAnyOfSeveralLiveIdentities()
    {
        CreateRegistration(_current);
        CreateRegistration(_keeper);

        Assert.Equal(
            0,
            StaleAppUserModelRegistrations.RemoveSuperseded(
                [_current, _keeper], [_current, _keeper]));
        Assert.True(Exists(_current));
        Assert.True(Exists(_keeper));
    }

    /// <summary>
    /// Registry key names are case-insensitive, so a guard that is not would compare unequal to the
    /// very key it is protecting.
    /// </summary>
    [Fact]
    public void TheLiveGuardIgnoresCase()
    {
        CreateRegistration(_current);

        Assert.Equal(
            0,
            StaleAppUserModelRegistrations.RemoveSuperseded(
                [_current.ToUpperInvariant()], [_current]));
        Assert.True(Exists(_current));
    }

    /// <summary>
    /// This runs on every launch, so all but the first run finds nothing to do. Already gone has to
    /// be a quiet zero rather than a throw or a phantom removal.
    /// </summary>
    [Fact]
    public void ItIsIdempotentOnceTheKeyIsGone()
    {
        CreateRegistration(_orphan);

        Assert.Equal(1, StaleAppUserModelRegistrations.RemoveSuperseded([_orphan], [_current]));
        Assert.Equal(0, StaleAppUserModelRegistrations.RemoveSuperseded([_orphan], [_current]));
        Assert.False(Exists(_orphan));
    }

    [Fact]
    public void AnAbsentKeyIsNotAnError()
    {
        Assert.Equal(0, StaleAppUserModelRegistrations.RemoveSuperseded([_orphan], [_current]));
    }

    /// <summary>
    /// A name the registry cannot use is not an error either. Whether it is rejected outright or
    /// simply matches nothing is the registry's business; what matters is that nothing propagates
    /// out of a call made on the startup path.
    /// </summary>
    [Fact]
    public void AnUnusableNameIsNotAnError()
    {
        Assert.Equal(
            0,
            StaleAppUserModelRegistrations.RemoveSuperseded([new string('x', 300)], [_current]));
    }

    /// <summary>
    /// A bad entry cannot take the rest of the list with it, or one addition would quietly turn the
    /// cleanup off for everything listed after it.
    /// </summary>
    [Fact]
    public void ABadEntryDoesNotStopTheRest()
    {
        CreateRegistration(_orphan);

        Assert.Equal(
            1,
            StaleAppUserModelRegistrations.RemoveSuperseded(
                ["", "   ", @"\", "/", @"\\", @"\some	hing", new string('x', 300), _orphan],
                [_current]));
        Assert.False(Exists(_orphan));
    }

    /// <summary>
    /// A name made only of separators must not reach DeleteSubKeyTree.
    ///
    /// It fixes up to the empty string, and DeleteSubKeyTree("") deletes the key the handle is open
    /// on - here the whole AppUserModelId hive, taking every desktop app's notification identity
    /// with it. Whitespace filtering does not catch it and neither does the existence probe, since
    /// OpenSubKey(@"\") returns the parent rather than null. Proven against a throwaway hive
    /// before the guard existed.
    /// </summary>
    [Theory]
    [InlineData(@"\")]
    [InlineData("/")]
    [InlineData(@"\\")]
    [InlineData(@"\   ")]
    public void ASeparatorEntryLeavesTheContainingKeyAlone(string aumid)
    {
        CreateRegistration(_orphan);

        Assert.Equal(0, StaleAppUserModelRegistrations.RemoveSuperseded([aumid], [_current]));

        using var classes = Registry.CurrentUser.OpenSubKey(Root);
        Assert.NotNull(classes);
        Assert.True(Exists(_orphan), "the containing key was deleted");
    }

    /// <summary>
    /// What the app actually ships. The danger is not the code, it is somebody adding a name to the
    /// list that a build still answers to, and no behaviour test can catch that.
    /// </summary>
    [Fact]
    public void TheShippedListNamesOnlyTheRenamedIdentity()
    {
        Assert.Equal(new[] { "com.deblasis.ghostty" }, StaleAppUserModelRegistrations.Superseded);
    }

    /// <summary>
    /// Stated separately from the exact list above, because this is the constraint that survives
    /// the list growing.
    ///
    /// Both families, not one. An untiered or public build registers under com.deblasis.wintty;
    /// every RELEASE build registers under ShipDigital.&lt;pack id&gt;, which wintty-release composes in
    /// builds/_common/patches from the resolved pack id. The first version of this guard named only
    /// the default family, so the one every shipped flavour actually uses was the one it did not
    /// cover - and comparing against AppIdentity.AumId does not close that, because it is the single
    /// variant this assembly happened to compile as.
    ///
    /// The failure it exists to stop: retire a flavour, add its AUMID here, and every other flavour
    /// deletes that one's registration on launch. Its toasts die until it is next started, which
    /// depends on the order the user opens things.
    /// </summary>
    [Theory]
    [InlineData("com.deblasis.wintty")]
    [InlineData("ShipDigital.")]
    public void TheShippedListNamesNothingInALiveIdentityFamily(string family)
    {
        foreach (var aumid in StaleAppUserModelRegistrations.Superseded)
        {
            Assert.False(
                aumid.StartsWith(family, StringComparison.OrdinalIgnoreCase),
                $"'{aumid}' is in the '{family}' AUMID family, which builds that can be installed "
                    + "today still register under");
            Assert.NotEqual(AppIdentity.AumId, aumid, StringComparer.OrdinalIgnoreCase);
        }
    }
}
