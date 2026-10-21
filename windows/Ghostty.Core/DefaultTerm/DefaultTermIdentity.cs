using System;

namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// The default-terminal identity this build carries, read off the
/// build-info channel (<c>BuildInfo.DefaultTerminalClsid</c> and
/// <c>BuildInfo.DerivedConsoleClsid</c>, baked by the GenerateBuildInfo
/// target; override at build time with the MSBuild properties of the
/// same names).
///
/// The nil GUID is the shipped default for a build that registers
/// nothing. It is a deliberate placeholder rather than a missing value:
/// <see cref="CanRegister"/> stays false while either CLSID is nil or
/// unparseable, so a build that never received real identity refuses
/// every registry write by construction instead of behind a feature
/// flag.
/// </summary>
internal static class DefaultTermIdentity
{
    /// <summary>
    /// The CLSID the shell resolves for this terminal once it is
    /// registered as a default terminal application. Braced GUID string.
    /// </summary>
    public static string TerminalClsid => _terminalClsid;

    /// <summary>
    /// The CLSID this terminal installs on the console class so a conpty
    /// handoff launches it in place of conhost. Braced GUID string.
    /// </summary>
    public static string ConsoleClsid => _consoleClsid;

    /// <summary>
    /// CLSID of the console proxy the default-terminal handoff chain goes
    /// through. Machine-verified constant, not build-configured.
    /// </summary>
    public const string ProxyClsid = "{3171DE52-6EFA-4AEF-8A9F-D02BD67E7A4F}";

    /// <summary>
    /// IIDs the handoff chain negotiates with the proxy, in negotiation
    /// order. Machine-verified constants, not build-configured.
    /// </summary>
    public static string[] HandoffIids { get; } = new[]
    {
        "{E686C757-9A35-4A1C-B3CE-0BCC8B5C69F4}",
        "{6F23DA90-15C5-4203-9DB0-64E73F1B1B00}",
        "{746E6BC0-AB05-4E38-AB14-71E86763141F}",
    };

    /// <summary>
    /// True while either CLSID is the nil placeholder or not a parseable
    /// GUID at all. Every registry-writing consumer gates on this.
    /// </summary>
    public static bool PlaceholdersActive =>
        IsNilOrUnparseable(_terminalClsid) || IsNilOrUnparseable(_consoleClsid);

    /// <summary>Inverse of <see cref="PlaceholdersActive"/>.</summary>
    public static bool CanRegister => !PlaceholdersActive;

    private static string _terminalClsid = Ghostty.Core.Version.BuildInfo.DefaultTerminalClsid;
    private static string _consoleClsid = Ghostty.Core.Version.BuildInfo.DerivedConsoleClsid;

    /// <summary>
    /// Test seam: replaces the build-baked CLSIDs. Process-wide state, so
    /// every test that reads this class arranges it first.
    /// </summary>
    internal static void SetForTest(string terminalClsid, string consoleClsid)
    {
        _terminalClsid = terminalClsid;
        _consoleClsid = consoleClsid;
    }

    private static bool IsNilOrUnparseable(string value) =>
        !Guid.TryParse(value, out Guid guid) || guid == Guid.Empty;
}
