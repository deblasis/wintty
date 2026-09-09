using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Where the toast-registration self-heal sits in startup.
///
/// The decision itself is pure and tested outright in
/// <c>ToastRegistrationTests</c>. What a parse adds is the ORDER, which no
/// test of the decision can see and which is the whole reason the repair
/// works: the removal has to run BEFORE <c>Register()</c>, because Register()
/// is what writes the key back and a removal that runs after it costs this
/// launch its notifications instead of fixing the next one. The rewrite has
/// to run AFTER, because the two values it corrects are ones Register()
/// derives from the process image and writes itself.
///
/// <c>App</c> lives in the WinUI project, which this assembly cannot
/// reference, so this parses the source the way the other wiring guards do.
/// </summary>
public class ToastRegistrationWiringTests
{
    private const string ShellFile = "App.xaml.cs";
    private const string Read = "Ghostty.Core.Windows.ToastRegistration.Read";
    private const string Diagnose = "Ghostty.Core.Windows.ToastRegistration.Diagnose";
    private const string Remove = "Ghostty.Core.Windows.ToastRegistration.Remove";
    private const string Apply = "Ghostty.Core.Windows.ToastRegistration.Apply";
    private const string Register =
        "Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register";

    private static BlockSyntax Launched() =>
        ShellSource.Load(ShellFile).Method("OnLaunched").Body!;

    private static int IndexOf(BlockSyntax body, string call) =>
        body.Statements.ToList().FindIndex(s => s.Calls(call).Count > 0);

    private static int LastIndexOf(BlockSyntax body, string call) =>
        body.Statements.ToList().FindLastIndex(s => s.Calls(call).Count > 0);

    [Fact]
    public void Startup_repairs_a_stale_registration_before_registering()
    {
        var body = Launched();

        var register = IndexOf(body, Register);
        var remove = IndexOf(body, Remove);

        Assert.True(register >= 0, "OnLaunched no longer registers for toast notifications");
        Assert.True(
            remove >= 0,
            "OnLaunched no longer removes a registration whose activator points at a departed "
            + "install; nothing else ever rewrites LocalServer32, so toast clicks stay dead.");
        Assert.True(
            remove < register,
            $"the removal is at statement {remove}, at or below Register() at {register}. "
                + "Register() is what writes the key back, so the removal has to precede it or "
                + "this launch loses its registration instead of repairing it.");
    }

    [Fact]
    public void The_shown_values_are_rewritten_after_registering()
    {
        var body = Launched();

        var register = IndexOf(body, Register);
        var apply = IndexOf(body, Apply);

        Assert.True(
            apply >= 0,
            "OnLaunched no longer rewrites the registration's DisplayName / IconUri.");
        Assert.True(
            apply > register,
            $"the rewrite is at statement {apply}, at or above Register() at {register}. "
                + "Register() derives those two from the process image and writes them itself, "
                + "so a rewrite before it is overwritten by it.");
    }

    /// <summary>
    /// Both halves read the registry again rather than reusing one snapshot.
    /// Register() runs between them and changes what is on the machine, so the
    /// rewrite's decision has to be made against what Register() left.
    /// </summary>
    [Fact]
    public void Each_half_diagnoses_against_a_fresh_read()
    {
        var body = Launched();

        Assert.Equal(2, body.Calls(Read).Count);
        Assert.Equal(2, body.Calls(Diagnose).Count);

        var register = IndexOf(body, Register);
        Assert.True(IndexOf(body, Read) < register, "the first read must precede Register()");
        Assert.True(LastIndexOf(body, Read) > register, "the second read must follow Register()");
    }

    /// <summary>
    /// The identity all four calls act on is the one the process was given,
    /// read from the same local rather than spelled a second time. A name that
    /// drifts here deletes a key that belongs to something else.
    /// </summary>
    [Fact]
    public void Every_call_names_the_identity_the_process_set()
    {
        var body = Launched();

        var identity = body.Call(
            "Windows.Win32.PInvoke.SetCurrentProcessExplicitAppUserModelID").Arg(0);

        foreach (var call in body.Calls(Read))
            Assert.Equal(identity, call.Arg(0));
        Assert.Equal(identity, body.Call(Remove).Arg(0));
        Assert.Equal(identity, body.Call(Apply).Arg(0));
    }

    /// <summary>
    /// The failure line carries the reason. A WinRT HRESULT arriving through a
    /// projected interface leaves frames that name the method and nothing that
    /// names the fault, which is how "Failed to register for toast
    /// notifications" stood in the log for a whole rehearsal saying nothing
    /// (deblasis/wintty-release#656).
    /// </summary>
    [Fact]
    public void The_registration_failure_logs_the_exception_message()
    {
        var source = ShellSource.Load(ShellFile);

        var template = source.Root.DescendantNodes()
            .OfType<AttributeSyntax>()
            .Where(a => a.Name.ToString() == "LoggerMessage")
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .Where(arg => arg.NameEquals?.Name.Identifier.ValueText == "Message")
            .Select(arg => arg.Expression.ToString())
            .SingleOrDefault(t => t.Contains(
                "Failed to register for toast notifications", System.StringComparison.Ordinal));

        Assert.True(template is not null, "the toast-registration failure message is gone");
        Assert.Contains("{Reason}", template!, System.StringComparison.Ordinal);

        foreach (var call in source.Root.Calls("Ghostty.Logging.StaticLoggers.App.LogToastRegisterFailed"))
        {
            Assert.Equal(2, call.ArgumentList.Arguments.Count);
            Assert.Equal("ex.Message", call.Arg(1));
        }
    }

    /// <summary>
    /// A refused removal is loud. Remove() swallows by contract, which is
    /// exactly why the call site has to say what was refused: without this
    /// line a refused delete left the launch half-repaired, the key still
    /// stale, and the next click still dead with nothing in the log.
    /// </summary>
    [Fact]
    public void A_refused_removal_is_logged_with_the_key_and_the_reason()
    {
        var source = ShellSource.Load(ShellFile);

        var call = source.Root.Calls(
            "Ghostty.Logging.StaticLoggers.App.LogToastRegistrationRemoveRefused")
            .SingleOrDefault();
        Assert.True(
            call is not null,
            "OnLaunched no longer logs a refused toast-registration removal; "
            + "Remove() never throws, so the call site is the only place a "
            + "half-repaired launch says so.");

        var identity = Launched().Call(
            "Windows.Win32.PInvoke.SetCurrentProcessExplicitAppUserModelID").Arg(0);
        Assert.Equal(identity, call!.Arg(0));
        // The reason argument is the removal's out reason, not a constant:
        // the argument is the local (with its null fallback spelled beside
        // it), and a literal here would log the same reason for every
        // refusal.
        Assert.StartsWith("toastRemoveRefusal", call.Arg(1), System.StringComparison.Ordinal);
    }
}
