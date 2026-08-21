using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Where the superseded-registration cleanup sits in startup.
///
/// It has to be on the launch path at all: nothing else ever removes an AppUserModelId key, so a
/// call site that gets dropped leaves the orphaned rows in Settings &gt; Notifications forever, and
/// no unit test of the remover can tell.
///
/// It also has to run before <c>Register()</c>. Register() recreates the key for this process's
/// identity, so a cleanup that runs first is recoverable if the list is ever wrong and one that
/// runs after is not. That is a fact about the ORDER, invisible to a test of the remover itself.
///
/// <c>App</c> lives in the WinUI project, which this assembly cannot reference, so this parses the
/// source the way the other wiring guards do.
/// </summary>
public class StaleAppUserModelWiringTests
{
    private const string ShellFile = "App.xaml.cs";
    private const string Removal =
        "Ghostty.Core.Windows.StaleAppUserModelRegistrations.RemoveSuperseded";
    private const string Register =
        "Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register";
    private const string SetIdentity =
        "Windows.Win32.PInvoke.SetCurrentProcessExplicitAppUserModelID";

    private static BlockSyntax Launched() =>
        ShellSource.Load(ShellFile).Method("OnLaunched").Body!;

    private static int IndexOf(BlockSyntax body, string call) =>
        body.Statements.ToList().FindIndex(s => s.Calls(call).Count > 0);

    [Fact]
    public void StartupRemovesSupersededRegistrations()
    {
        Launched().Call(Removal);
    }

    [Fact]
    public void TheRemovalRunsBeforeTheToastRegistration()
    {
        var body = Launched();

        var register = IndexOf(body, Register);
        var removal = IndexOf(body, Removal);

        Assert.True(register >= 0, "OnLaunched no longer registers for toast notifications");
        Assert.True(removal >= 0, "OnLaunched no longer removes superseded registrations");
        Assert.True(
            removal < register,
            $"the removal is at statement {removal}, at or below Register() at {register}. "
                + "Register() is what recreates this process's own key, so running the removal "
                + "first is what keeps a wrong entry in the list from costing this launch its "
                + "notifications.");
    }

    /// <summary>
    /// It names the identity this process answers to, and names it by reading the same constant
    /// the process was given rather than spelling the AUMID a second time. The remover refuses to
    /// delete what it is told is current, so an argument that drifts is the one way that guard
    /// stops guarding anything.
    /// </summary>
    [Fact]
    public void TheRemovalNamesTheSameIdentityTheProcessSet()
    {
        var body = Launched();

        var identityArg = body.Call(SetIdentity).Arg(0);
        var removalArg = body.Call(Removal).Arg(0);

        Assert.Equal(identityArg, removalArg);
    }
}
