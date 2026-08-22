using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Where the AppUserModelId correction sits in startup, which is the whole of whether it works.
///
/// <c>AppNotificationManager.Register()</c> rewrites the key's <c>IconUri</c> with a 16x16 toast
/// PNG on EVERY launch, not only the first. So the correction is not a thing that is true or false
/// about the code, it is a thing that is true or false about the ORDER: run it before Register()
/// and it is silently reverted, run it after and it holds. That ordering cannot be observed from a
/// unit test of the writer, and the symptom of getting it wrong - an upscaled icon in a Start list
/// - is invisible in every automated check we have.
///
/// <c>App</c> lives in the WinUI project, which this assembly cannot reference, so this parses the
/// source the way the other wiring guards do.
/// </summary>
public class AppUserModelIconWiringTests
{
    private const string ShellFile = "App.xaml.cs";

    private static BlockSyntax Launched() =>
        ShellSource.Load(ShellFile).Method("OnLaunched").Body!;

    private static int IndexOf(BlockSyntax body, string call) =>
        body.Statements.ToList().FindIndex(s => s.Calls(call).Count > 0);

    [Fact]
    public void StartupCorrectsTheAppUserModelRegistration()
    {
        Launched().Call("Ghostty.Core.Windows.AppUserModelRegistration.Apply");
    }

    /// <summary>
    /// After Register(), because Register() overwrites what this writes.
    /// </summary>
    [Fact]
    public void TheCorrectionRunsAfterTheToastRegistration()
    {
        var body = Launched();

        var register = IndexOf(
            body, "Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register");
        var apply = IndexOf(body, "Ghostty.Core.Windows.AppUserModelRegistration.Apply");

        Assert.True(register >= 0, "OnLaunched no longer registers for toast notifications");
        Assert.True(apply >= 0, "OnLaunched no longer corrects the AppUserModelId registration");
        Assert.True(
            apply > register,
            $"the correction is at statement {apply}, at or above Register() at {register}. "
                + "Register() rewrites IconUri with the toast-sized PNG on every launch, so a "
                + "correction that runs first is reverted before the user ever sees it.");
    }

    /// <summary>
    /// And after the process AUMID is set, since the key being corrected is the one Register()
    /// derived from that identity. A reorder here would correct another flavour's registration.
    /// </summary>
    [Fact]
    public void TheCorrectionRunsAfterTheProcessIdentityIsSet()
    {
        var body = Launched();

        var identity = IndexOf(body, "Windows.Win32.PInvoke.SetCurrentProcessExplicitAppUserModelID");
        var apply = IndexOf(body, "Ghostty.Core.Windows.AppUserModelRegistration.Apply");

        Assert.True(identity >= 0, "OnLaunched no longer sets an explicit AUMID");
        Assert.True(apply > identity, "the correction runs before the process identity is set");
    }

    /// <summary>
    /// It corrects the identity this process actually answers to.
    ///
    /// Passing the AUMID in rather than having the writer read it back is what keeps the two in
    /// step; a literal here would be a second place the AUMID is spelled, and the shell merges
    /// installs that share one.
    /// </summary>
    [Fact]
    public void TheCorrectionNamesTheSameIdentityTheProcessSet()
    {
        var body = Launched();

        var identityArg = body
            .Call("Windows.Win32.PInvoke.SetCurrentProcessExplicitAppUserModelID").Arg(0);
        var appliedArg = body.Call("Ghostty.Core.Windows.AppUserModelRegistration.Apply").Arg(0);

        Assert.Equal(identityArg, appliedArg);
    }
}
