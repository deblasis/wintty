using Ghostty.Core.DefaultTerm;
using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The identity channel is baked in at build time and a build that does
/// not carry real GUIDs ships the nil placeholder, so registration has
/// to refuse until both CLSIDs are present and parseable. Pure in-memory:
/// these tests read constants and one internal test seam and never touch
/// the registry.
/// </summary>
public sealed class DefaultTermIdentityTests
{
    [Fact]
    public void NilDefaults_RefuseRegistration()
    {
        // Arrange from whatever the build baked in, so the assertion is
        // about the default channel and not about leftover seam state.
        DefaultTermIdentity.SetForTest(
            BuildInfo.DefaultTerminalClsid,
            BuildInfo.DerivedConsoleClsid);

        Assert.Equal("{00000000-0000-0000-0000-000000000000}", BuildInfo.DefaultTerminalClsid);
        Assert.Equal("{00000000-0000-0000-0000-000000000000}", BuildInfo.DerivedConsoleClsid);
        Assert.True(DefaultTermIdentity.PlaceholdersActive);
        Assert.False(DefaultTermIdentity.CanRegister);
    }

    [Fact]
    public void WellFormedClsids_EnableRegistration()
    {
        DefaultTermIdentity.SetForTest(
            "{11111111-2222-3333-4444-555555555555}",
            "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}");

        Assert.Equal("{11111111-2222-3333-4444-555555555555}", DefaultTermIdentity.TerminalClsid);
        Assert.Equal("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", DefaultTermIdentity.ConsoleClsid);
        Assert.False(DefaultTermIdentity.PlaceholdersActive);
        Assert.True(DefaultTermIdentity.CanRegister);
    }

    [Fact]
    public void ProxyAndIidConstants_AreTheVerifiedOnes()
    {
        Assert.Equal("{3171DE52-6EFA-4AEF-8A9F-D02BD67E7A4F}", DefaultTermIdentity.ProxyClsid);
        Assert.Equal(new[]
        {
            "{E686C757-9A35-4A1C-B3CE-0BCC8B5C69F4}",
            "{6F23DA90-15C5-4203-9DB0-64E73F1B1B00}",
            "{746E6BC0-AB05-4E38-AB14-71E86763141F}",
        }, DefaultTermIdentity.HandoffIids);
    }
}
