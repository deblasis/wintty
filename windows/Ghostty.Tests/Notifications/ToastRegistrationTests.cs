using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Ghostty.Core.Windows;
using Microsoft.Win32;
using Xunit;

namespace Ghostty.Tests.Notifications;

/// <summary>
/// The decision behind the toast-registration self-heal.
///
/// <c>AppNotificationManager.Register()</c> writes the AUMID key once and
/// never revisits it, so an install that follows another one re-registers
/// against the first one's values: a <c>CustomActivator</c> whose
/// <c>LocalServer32</c> names an exe that is gone, and a <c>DisplayName</c>
/// that is the other install's (deblasis/wintty-release#656). Nothing in the
/// process notices, because <c>Show()</c> keeps working against the stale
/// registration and only the click is dead.
///
/// These cover <see cref="ToastRegistration.Diagnose"/>, which is the whole
/// decision and is pure. What it decides is destructive - a Recreate deletes
/// registry keys unattended on a user's machine - so the cases that matter
/// most here are the ones where it must decide to do NOTHING.
///
/// The registry halves around the decision are covered too, against the real
/// HKCU hive under names minted for the one test instance and taken back out
/// in Dispose: Read, Remove and Apply are thin, but one of them deletes keys
/// unattended, so what they actually do to the hive is pinned, not assumed.
/// The shared parents (AppUserModelId, CLSID) are never deleted.
/// </summary>
[SupportedOSPlatform("windows")]
public class ToastRegistrationTests : IDisposable
{
    private const string Exe = @"C:\Program Files\Wintty\wintty.exe";
    private const string Clsid = "{1B7C4E2A-0000-0000-0000-00000000BEEF}";

    private static ToastRegistrationValues Registered(
        string? displayName = "Wintty",
        string? iconUri = null,
        string? clsid = Clsid,
        string? server = "\"" + Exe + "\" ----AppNotificationActivated:")
        => new(Exists: true, displayName, iconUri, clsid, server);

    // --- the cases that must not touch anything ---

    [Fact]
    public void A_machine_with_no_registration_needs_no_repair()
    {
        var repair = ToastRegistration.Diagnose(default, Exe, "Wintty", null);

        // Register() is what creates the key. Reporting work here would delete
        // nothing and say so on every launch of a clean machine.
        Assert.False(repair.Any);
    }

    [Fact]
    public void A_registration_that_already_names_this_exe_is_left_alone()
    {
        var repair = ToastRegistration.Diagnose(Registered(), Exe, "Wintty", null);

        Assert.False(repair.Any);
    }

    [Fact]
    public void An_unknown_process_path_never_recreates()
    {
        // Environment.ProcessPath can be null. "I do not know where I am" is
        // not evidence that the activator is wrong, and acting on it would
        // delete a healthy registration.
        Assert.False(ToastRegistration.Diagnose(Registered(), null, "Wintty", null).Recreate);
        Assert.False(ToastRegistration.Diagnose(Registered(), "  ", "Wintty", null).Recreate);
    }

    [Fact]
    public void A_caller_with_no_opinion_leaves_the_shown_values_alone()
    {
        var observed = Registered(displayName: "Something Else", iconUri: "file:///old.png");

        var repair = ToastRegistration.Diagnose(observed, Exe, null, null);

        Assert.False(repair.RewriteDisplayName);
        Assert.False(repair.RewriteIconUri);
    }

    // --- the reported defects ---

    [Fact]
    public void An_activator_pointing_at_a_departed_install_is_recreated()
    {
        var observed = Registered(
            server: "\"C:\\wt\\sandbox-august\\wintty.exe\" ----AppNotificationActivated:");

        var repair = ToastRegistration.Diagnose(observed, Exe, "Wintty", null);

        Assert.True(repair.Recreate);
    }

    [Fact]
    public void A_key_with_no_activator_at_all_is_recreated()
    {
        // It can never deliver a click either, and it is the same repair:
        // let Register() write the pair again.
        Assert.True(ToastRegistration.Diagnose(Registered(clsid: null), Exe, "Wintty", null).Recreate);
        Assert.True(ToastRegistration.Diagnose(Registered(server: null), Exe, "Wintty", null).Recreate);
        Assert.True(ToastRegistration.Diagnose(Registered(server: "   "), Exe, "Wintty", null).Recreate);
    }

    [Fact]
    public void A_display_name_from_another_edition_is_rewritten()
    {
        // The reported shape: Pro and Enterprise installed over a build that
        // called itself "Wintty", and every toast kept saying so.
        var repair = ToastRegistration.Diagnose(Registered(), Exe, "Wintty Pro", null);

        Assert.True(repair.RewriteDisplayName);
        // Independent of the activator: that one was fine here.
        Assert.False(repair.Recreate);
    }

    [Fact]
    public void The_display_name_is_compared_exactly()
    {
        // Case is what distinguishes the tiers on screen, so a case-only
        // difference is a real difference here, unlike a path.
        Assert.True(ToastRegistration.Diagnose(Registered("wintty"), Exe, "Wintty", null).RewriteDisplayName);
        Assert.False(ToastRegistration.Diagnose(Registered("Wintty"), Exe, "Wintty", null).RewriteDisplayName);
    }

    [Fact]
    public void An_icon_from_another_install_is_rewritten()
    {
        var observed = Registered(iconUri: "file:///C:/wt/sandbox-august/icon.png");

        var repair = ToastRegistration.Diagnose(observed, Exe, "Wintty", "file:///C:/Program%20Files/Wintty/icon.png");

        Assert.True(repair.RewriteIconUri);
    }

    [Fact]
    public void An_absent_icon_counts_as_a_disagreement_when_one_is_wanted()
    {
        var repair = ToastRegistration.Diagnose(Registered(iconUri: null), Exe, "Wintty", "file:///icon.png");

        Assert.True(repair.RewriteIconUri);
    }

    // --- reading the activator command line ---

    [Theory]
    // The shape the platform writes: quoted path, activation argument after.
    [InlineData("\"C:\\a b\\wintty.exe\" ----AppNotificationActivated:", "C:\\a b\\wintty.exe")]
    // No arguments, still quoted.
    [InlineData("\"C:\\a b\\wintty.exe\"", "C:\\a b\\wintty.exe")]
    // Unquoted is taken whole. A path with a space and no quotes is already
    // broken; guessing where it ends would invent a disagreement out of a
    // value nothing can use anyway.
    [InlineData("C:\\dir\\wintty.exe", "C:\\dir\\wintty.exe")]
    [InlineData("  C:\\dir\\wintty.exe  ", "C:\\dir\\wintty.exe")]
    public void The_server_executable_is_read_off_the_command_line(string command, string expected)
    {
        Assert.Equal(expected, ToastRegistration.ServerExecutable(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // An unterminated quote names nothing rather than everything: reading the
    // rest of the line as a path would compare a path against a command line.
    [InlineData("\"C:\\dir\\wintty.exe")]
    public void An_unusable_command_line_names_no_executable(string? command)
    {
        Assert.Null(ToastRegistration.ServerExecutable(command));
    }

    // --- comparing the two paths ---

    [Theory]
    [InlineData(@"C:\Dir\Wintty.exe", @"c:\dir\wintty.exe")]
    [InlineData(@"C:/Dir/Wintty.exe", @"C:\Dir\Wintty.exe")]
    [InlineData(@"  C:\Dir\Wintty.exe  ", @"C:\Dir\Wintty.exe")]
    [InlineData("\"C:\\Dir\\Wintty.exe\"", @"C:\Dir\Wintty.exe")]
    public void Paths_that_differ_only_the_ways_windows_does_not_care_about_match(string a, string b)
    {
        Assert.True(ToastRegistration.SameExecutable(a, b));
    }

    [Theory]
    [InlineData(@"C:\Dir\Wintty.exe", @"C:\Other\Wintty.exe")]
    [InlineData(null, @"C:\Dir\Wintty.exe")]
    [InlineData(@"C:\Dir\Wintty.exe", null)]
    [InlineData("", "")]
    public void Anything_else_is_a_different_executable(string? a, string? b)
    {
        // Including two absences: "neither of us knows" is not a match, or an
        // unreadable activator would read as healthy.
        Assert.False(ToastRegistration.SameExecutable(a, b));
    }

    // --- the registry halves, against a throwaway corner of the real hive ---

    private const string AumidRoot = @"Software\Classes\AppUserModelId";
    private const string ClsidRoot = @"Software\Classes\CLSID";

    private readonly string _aumid = $"Ghostty.Tests.ToastRegistration.{Guid.NewGuid():N}";
    private readonly string _otherAumid = $"Ghostty.Tests.ToastRegistration.{Guid.NewGuid():N}";
    private readonly string _clsid = $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}";
    private readonly string _otherClsid = $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}";
    private readonly List<string> _created = new();

    public void Dispose()
    {
        foreach (var path in _created)
        {
            try
            {
                var cut = path.LastIndexOf('\\');
                using var parent = Registry.CurrentUser.OpenSubKey(path[..cut], writable: true);
                parent?.DeleteSubKeyTree(path[(cut + 1)..], throwOnMissingSubKey: false);
            }
            catch
            {
                // Teardown is best effort; a failed cleanup must not fail the run.
            }
        }
    }

    private void CreateAumidKey(
        string name, string? displayName = null, string? iconUri = null, string? activator = null)
    {
        _created.Add($@"{AumidRoot}\{name}");
        using var key = Registry.CurrentUser.CreateSubKey($@"{AumidRoot}\{name}");
        if (displayName is not null) key.SetValue("DisplayName", displayName, RegistryValueKind.String);
        if (iconUri is not null) key.SetValue("IconUri", iconUri, RegistryValueKind.String);
        if (activator is not null) key.SetValue("CustomActivator", activator, RegistryValueKind.String);
    }

    private void CreateClsidKey(string name, string localServer32)
    {
        _created.Add($@"{ClsidRoot}\{name}");
        using var key = Registry.CurrentUser.CreateSubKey($@"{ClsidRoot}\{name}\LocalServer32");
        key.SetValue(null, localServer32, RegistryValueKind.String);
    }

    private static bool KeyExists(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null;
    }

    private static string? ValueOf(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    [Fact]
    public void Read_returns_the_key_and_its_activator()
    {
        CreateClsidKey(_clsid, $"\"{Exe}\" ----AppNotificationActivated:");
        CreateAumidKey(_aumid, displayName: "Wintty", iconUri: "file:///icon.png", activator: _clsid);

        var read = ToastRegistration.Read(_aumid);

        Assert.True(read.Exists);
        Assert.Equal("Wintty", read.DisplayName);
        Assert.Equal("file:///icon.png", read.IconUri);
        Assert.Equal(_clsid, read.ActivatorClsid);
        Assert.Equal($"\"{Exe}\" ----AppNotificationActivated:", read.ActivatorServer);
    }

    [Fact]
    public void Read_of_a_key_that_is_not_there_asks_for_nothing()
    {
        // Nothing created this instance's key: an unreadable hive reads as
        // "no key", which asks for no repair.
        Assert.False(ToastRegistration.Read(_aumid).Exists);
    }

    [Fact]
    public void Remove_deletes_only_the_aumid_key_and_the_one_clsid_it_names()
    {
        // Two AUMIDs and two COM classes under the shared parents, the shape
        // a machine with two installs actually has. The repair names one of
        // each; the neighbours are what an overbroad delete would take with
        // it - the "nothing else under the parent" half of the contract.
        CreateAumidKey(_aumid, activator: _clsid);
        CreateAumidKey(_otherAumid, displayName: "someone else");
        CreateClsidKey(_clsid, $"\"{Exe}\" ----AppNotificationActivated:");
        CreateClsidKey(_otherClsid, "C:\\elsewhere\\other.exe");

        Assert.True(ToastRegistration.Remove(_aumid, _clsid, out var refused));
        Assert.Null(refused);

        Assert.False(KeyExists($@"{AumidRoot}\{_aumid}"));
        Assert.False(KeyExists($@"{ClsidRoot}\{_clsid}"));
        Assert.True(KeyExists($@"{AumidRoot}\{_otherAumid}"));
        Assert.True(KeyExists($@"{ClsidRoot}\{_otherClsid}"));
    }

    [Fact]
    public void Remove_of_a_key_that_is_not_there_removes_nothing_and_refuses_nothing()
    {
        Assert.False(ToastRegistration.Remove(_aumid, _clsid, out var refused));
        Assert.Null(refused);
    }

    [Fact]
    public void A_name_unsafe_for_a_subkey_operation_never_reaches_the_registry()
    {
        // The hazard IsPlainAumid names: a name made of separators fixes up
        // to the empty string, and DeleteSubKeyTree on that deletes the key
        // the handle is open on - here the whole AppUserModelId or CLSID
        // hive, with every other install's identity in it. The guard refuses
        // first and both hives keep all their tenants.
        CreateAumidKey(_aumid, displayName: "kept");
        CreateClsidKey(_clsid, $"\"{Exe}\" ----AppNotificationActivated:");

        Assert.False(ToastRegistration.Remove(@"a\b", _clsid, out var refused));
        Assert.NotNull(refused);
        Assert.True(KeyExists(AumidRoot));
        Assert.True(KeyExists($@"{AumidRoot}\{_aumid}"));
        Assert.True(KeyExists(ClsidRoot));
        Assert.True(KeyExists($@"{ClsidRoot}\{_clsid}"));

        // The same refusal at the other two doors, and the other separator.
        Assert.False(ToastRegistration.Read("a/b").Exists);
        Assert.False(ToastRegistration.Apply(
            @"a\b", new ToastRegistrationRepair(false, true, true), "Wintty", null));
    }

    [Fact]
    public void Apply_writes_what_the_repair_names()
    {
        CreateAumidKey(_aumid, displayName: "Someone Else", iconUri: "file:///old.png");

        Assert.True(ToastRegistration.Apply(
            _aumid,
            new ToastRegistrationRepair(Recreate: false, RewriteDisplayName: true, RewriteIconUri: true),
            "Wintty Pro", "file:///new.png"));

        Assert.Equal("Wintty Pro", ValueOf($@"{AumidRoot}\{_aumid}", "DisplayName"));
        Assert.Equal("file:///new.png", ValueOf($@"{AumidRoot}\{_aumid}", "IconUri"));
    }

    [Fact]
    public void Apply_with_nothing_to_rewrite_touches_nothing()
    {
        CreateAumidKey(_aumid, displayName: "Someone Else");

        Assert.False(ToastRegistration.Apply(_aumid, default, "Wintty Pro", "file:///new.png"));

        Assert.Equal("Someone Else", ValueOf($@"{AumidRoot}\{_aumid}", "DisplayName"));
        Assert.Null(ValueOf($@"{AumidRoot}\{_aumid}", "IconUri"));
    }

    [Fact]
    public void Apply_to_a_key_that_is_not_there_writes_nothing()
    {
        Assert.False(ToastRegistration.Apply(
            _aumid,
            new ToastRegistrationRepair(Recreate: false, RewriteDisplayName: true, RewriteIconUri: false),
            "Wintty Pro", null));
    }
}
