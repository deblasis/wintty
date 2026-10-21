using Ghostty.Core.DefaultTerm;
using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The register-receive-classify-revoke lifecycle of the class object and
/// the bootstrap conditions that gate it, all against injected seams: no
/// test registers a real class object, reads a token, or touches the
/// registry. What this pins: the four permanent-registration conditions of
/// the spec (election winner, enabled, bound path, not elevated), the
/// embedding spellings, the classification state machine including the
/// elevated receiver's never-permanent rule, the shutdown ordering
/// (revoke, then drain, then release), and the idle-embedding exit code
/// being a distinct entry in the process exit-code taxonomy.
/// </summary>
public sealed class LifecycleWiringTests
{
    [Fact]
    public void ElevationCondition_BlocksPermanentRegistration()
    {
        var ctx = ValidPrimaryContext();
        ctx = ctx with { IntegrityLevel = () => TokenIntegrity.High };

        Assert.False(DefaultTermBootstrap.ShouldRegister(ctx));

        DefaultTermComServer.Reset();
        DefaultTermComServer.TryRegisterPermanent(ctx);
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
    }

    [Fact]
    public void UnboundExe_DoesNotRegister()
    {
        var ctx = ValidPrimaryContext() with { BoundPathCheck = () => false };

        Assert.False(DefaultTermBootstrap.ShouldRegister(ctx));

        DefaultTermComServer.Reset();
        DefaultTermComServer.TryRegisterPermanent(ctx);
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
    }

    [Fact]
    public void DisabledEdition_DoesNotRegister()
    {
        var ctx = ValidPrimaryContext() with { CanRegister = false };

        Assert.False(DefaultTermBootstrap.ShouldRegister(ctx));
        Assert.False(DefaultTermBootstrap.ShouldRegisterTransient(ctx));

        DefaultTermComServer.Reset();
        DefaultTermComServer.TryRegisterPermanent(ctx);
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
    }

    [Fact]
    public void EmbeddingSpellings_BothRecognized()
    {
        Assert.True(DefaultTermBootstrap.IsEmbeddingLaunch(new[] { "-Embedding" }));
        Assert.True(DefaultTermBootstrap.IsEmbeddingLaunch(new[] { "/Embedding" }));
        Assert.False(DefaultTermBootstrap.IsEmbeddingLaunch(new[] { "--embed" }));
        Assert.False(DefaultTermBootstrap.IsEmbeddingLaunch(Array.Empty<string>()));
    }

    [Fact]
    public void Classify_MediumNoPrimary_UpgradesToPermanent()
    {
        DefaultTermComServer.Reset();
        var log = new List<string>();
        DefaultTermComServer.RegisterOverride = (_, kind) =>
        {
            log.Add("register:" + kind);
            return 1;
        };
        DefaultTermComServer.RevokeOverride = _ => log.Add("revoke");

        DefaultTermComServer.RegisterTransient(NewGuid());
        Assert.Equal(RegistrationKind.Transient, DefaultTermComServer.Kind);

        var classification = DefaultTermComServer.ClassifyReceiver(
            elevated: false, otherPrimaryExists: false);

        Assert.Equal(ReceiverClassification.UpgradedToPrimary, classification);
        Assert.Equal(RegistrationKind.Permanent, DefaultTermComServer.Kind);
        // The consumed transient is defensively revoked before the
        // permanent registration goes in.
        Assert.Equal(new[] { "register:Transient", "revoke", "register:Permanent" }, log);
    }

    [Fact]
    public void Classify_Elevated_NeverPermanent()
    {
        DefaultTermComServer.Reset();
        DefaultTermComServer.RegisterOverride = (_, _) => 1;
        DefaultTermComServer.RevokeOverride = _ => { };

        DefaultTermComServer.RegisterTransient(NewGuid());
        Assert.Equal(RegistrationKind.Transient, DefaultTermComServer.Kind);

        var classification = DefaultTermComServer.ClassifyReceiver(
            elevated: true, otherPrimaryExists: false);

        // The transient was consumed by the activation it served; an
        // elevated receiver never holds a class object afterwards and no
        // path promotes it to permanent.
        Assert.Equal(ReceiverClassification.ElevatedSession, classification);
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);

        DefaultTermComServer.TryRegisterPermanent(
            ValidPrimaryContext() with { IntegrityLevel = () => TokenIntegrity.High });
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
    }

    [Fact]
    public void Shutdown_RevokeDrainsThenReleases()
    {
        DefaultTermComServer.Reset();
        var log = new List<string>();
        DefaultTermComServer.RegisterOverride = (_, _) => 7;
        DefaultTermComServer.RevokeOverride = cookie => log.Add("revoke:" + cookie);
        DefaultTermComServer.DrainOverride = () => log.Add("drain");
        DefaultTermComServer.ReleaseOverride = () => log.Add("release");

        DefaultTermComServer.RegisterTransient(NewGuid());
        DefaultTermComServer.Shutdown();

        // Revoking first stops new activations; the drain then settles the
        // handoffs already in flight; only afterwards is COM released.
        Assert.Equal(new[] { "revoke:7", "drain", "release" }, log);
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
    }

    [Fact]
    public void IdleEmbedding_ExitsCleanly()
    {
        DefaultTermComServer.Reset();
        DefaultTermComServer.RegisterOverride = (_, _) => 1;
        DefaultTermComServer.RevokeOverride = _ => { };
        DefaultTermComServer.RegisterTransient(NewGuid());

        var exitCode = DefaultTermComServer.ExitIdleEmbedding();

        // A distinct taxonomy entry: not success, not the crash, usage,
        // init, test-config or unhandled codes the process already uses.
        Assert.Equal(DefaultTermComServer.IdleEmbeddingExitCode, exitCode);
        Assert.DoesNotContain(exitCode, new[] { 0, 1, 2, 3, 4 });
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
    }

    [Fact]
    public void RegistrationFailure_IsNotFatal()
    {
        // A failed CoRegisterClassObject leaves nothing registered and
        // never unwinds into the caller: an enabled install must still
        // start the app when one COM call refuses.
        DefaultTermComServer.Reset();
        var diagnosed = new List<string>();
        DefaultTermComServer.Diagnostic = diagnosed.Add;
        DefaultTermComServer.RegisterOverride = (_, _) =>
            throw new InvalidOperationException("CoRegisterClassObject refused");

        DefaultTermComServer.TryRegisterPermanent(ValidPrimaryContext());

        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
        Assert.Single(diagnosed);

        var classification = DefaultTermComServer.ClassifyReceiver(
            elevated: false, otherPrimaryExists: false);

        // The upgrade failed the same non-fatal way: nothing registered,
        // nothing thrown.
        Assert.Equal(ReceiverClassification.UpgradedToPrimary, classification);
        Assert.Equal(RegistrationKind.None, DefaultTermComServer.Kind);
    }

    /// <summary>
    /// A context that satisfies every permanent-registration condition:
    /// election won, enabled, registrable edition, bound path, medium
    /// integrity.
    /// </summary>
    private static BootstrapContext ValidPrimaryContext() => new(
        Role: SingleInstanceRole.Primary,
        FeatureEnabled: true,
        CanRegister: true,
        IntegrityLevel: () => TokenIntegrity.Medium,
        BoundPathCheck: () => true);

    private static Guid NewGuid() => Guid.NewGuid();
}
