using System;
using System.IO;
using Ghostty.Core.Windows;
using Microsoft.Win32;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// Writes to the real registry, under an AUMID unique to the run, and removes it afterwards.
/// The behaviour under test IS the registry write, so a fake would only restate the code.
/// </summary>
public sealed class AppUserModelRegistrationTests : IDisposable
{
    private const string Root = @"Software\Classes\AppUserModelId";

    private readonly string _aumid = "Ghostty.Tests." + Guid.NewGuid().ToString("N");
    private readonly string _iconPath = Path.Combine(
        Path.GetTempPath(), "wintty-aumid-test-" + Guid.NewGuid().ToString("N") + ".ico");

    public void Dispose()
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(Root, writable: true);
            classes?.DeleteSubKeyTree(_aumid, throwOnMissingSubKey: false);
        }
        catch (Exception) { }

        try { if (File.Exists(_iconPath)) File.Delete(_iconPath); } catch (Exception) { }
    }

    private RegistryKey CreateRegistration()
    {
        using var classes = Registry.CurrentUser.CreateSubKey(Root, writable: true)!;
        var key = classes.CreateSubKey(_aumid, writable: true)!;
        // What Register() leaves behind, and what this code has to correct.
        key.SetValue("IconUri", @"C:\nowhere\toast-sized.png", RegistryValueKind.String);
        key.SetValue("DisplayName", "Wintty", RegistryValueKind.String);
        return key;
    }

    private void WriteIcon() => File.WriteAllBytes(_iconPath, new byte[] { 0, 0, 1, 0, 1, 0 });

    [Fact]
    public void ItReplacesTheToastIconAndTheName()
    {
        using (CreateRegistration()) { }
        WriteIcon();

        Assert.True(AppUserModelRegistration.Apply(_aumid, "Wintty Pro", _iconPath));

        using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{_aumid}")!;
        Assert.Equal(_iconPath, key.GetValue("IconUri"));
        Assert.Equal("Wintty Pro", key.GetValue("DisplayName"));
    }

    /// <summary>
    /// Register() rewrites IconUri on every launch, so this has to be able to run repeatedly and
    /// land on the same answer. A first-run-only guard would leave the toast PNG in place from the
    /// second launch onward, which is the failure that makes an install-time fix impossible.
    /// </summary>
    [Fact]
    public void ItIsIdempotentAcrossRelaunches()
    {
        using (CreateRegistration()) { }
        WriteIcon();

        Assert.True(AppUserModelRegistration.Apply(_aumid, "Wintty Pro", _iconPath));

        // Register() runs again and clobbers it.
        using (var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{_aumid}", writable: true)!)
        {
            key.SetValue("IconUri", @"C:\nowhere\toast-sized.png", RegistryValueKind.String);
            key.SetValue("DisplayName", "Wintty", RegistryValueKind.String);
        }

        Assert.True(AppUserModelRegistration.Apply(_aumid, "Wintty Pro", _iconPath));

        using var after = Registry.CurrentUser.OpenSubKey($@"{Root}\{_aumid}")!;
        Assert.Equal(_iconPath, after.GetValue("IconUri"));
        Assert.Equal("Wintty Pro", after.GetValue("DisplayName"));
    }

    /// <summary>
    /// A missing icon leaves IconUri alone rather than writing a path that resolves to nothing.
    /// A broken path is worse than the small icon: Start has nothing left to fall back to.
    /// </summary>
    [Fact]
    public void AMissingIconLeavesTheExistingOneAlone()
    {
        using (CreateRegistration()) { }

        Assert.True(AppUserModelRegistration.Apply(_aumid, "Wintty Pro", _iconPath));

        using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{_aumid}")!;
        Assert.Equal(@"C:\nowhere\toast-sized.png", key.GetValue("IconUri"));
        Assert.Equal("Wintty Pro", key.GetValue("DisplayName"));
    }

    /// <summary>
    /// No key means Register() did not run. Creating one here would leave a registration carrying
    /// an icon and a name but no CustomActivator - complete to look at, and activating nothing.
    /// </summary>
    [Fact]
    public void ItDoesNotCreateAKeyRegisterNeverMade()
    {
        WriteIcon();

        Assert.False(AppUserModelRegistration.Apply(_aumid, "Wintty Pro", _iconPath));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{Root}\{_aumid}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ItRefusesAnEmptyAumid(string aumid)
    {
        Assert.False(AppUserModelRegistration.Apply(aumid, "Wintty Pro", _iconPath));
    }
}
