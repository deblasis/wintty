using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// ConfigChangeFanOut is only worth anything if the notification sites
/// actually route through it, and Ghostty.Tests cannot reference the shell
/// project to assert that directly. Without this scan, reverting a call
/// site to a bare <c>ConfigChanged?.Invoke(this)</c> reintroduces a process
/// kill and leaves the whole suite green -- the fan-out's own tests pass
/// happily whether or not anybody calls it.
///
/// The scan is deliberately a "no bare invoke survives" check rather than a
/// count, so a new fan-out site added later is caught as well.
/// </summary>
public class ConfigChangeFanOutWiringTests
{
    /// <summary>
    /// Matches a direct multicast invoke of one of the notification events,
    /// which is the shape that fail-fasts. The fan-out's own call is written
    /// <c>InvokeAll(ConfigChanged, ...)</c>, so it does not match.
    /// </summary>
    private static readonly Regex BareInvoke = new(
        @"\b(ConfigChanged|ProfileConfigChanged|ProfilesChanged)\s*\?\s*\.\s*Invoke\s*\(",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("Services/ConfigService.cs")]
    [InlineData("Profiles/ProfileRegistry.cs")]
    public void NotificationSites_GoThroughTheFanOut(string suffix)
    {
        var source = StripComments(ReadEmbedded(suffix));

        var offenders = BareInvoke.Matches(source)
            .Select(m => m.Value)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{suffix} invokes a config-change event directly: {string.Join(", ", offenders)}. " +
            "These fan-outs run as dispatcher callbacks, where an escaping exception " +
            "fail-fasts the process. Route them through ConfigChangeFanOut.InvokeAll.");
    }

    [Theory]
    [InlineData("Services/ConfigService.cs")]
    [InlineData("Profiles/ProfileRegistry.cs")]
    public void NotificationSites_ActuallyCallTheFanOut(string suffix)
    {
        // Paired with the check above so deleting the notification entirely
        // cannot pass by having nothing left to match.
        var source = StripComments(ReadEmbedded(suffix));
        Assert.Contains("ConfigChangeFanOut.InvokeAll(", source);
    }

    /// <summary>
    /// The strip is what makes the scan honest: the file explains this
    /// hazard in prose, and an unstripped scan would match the explanation
    /// and pass regardless of what the code does.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\n]*", string.Empty);
    }

    /// <summary>
    /// The source glob builds logical names from MSBuild's RecursiveDir,
    /// which keeps its backslashes, so the directory separator survives into
    /// the resource name. Matching on it is not cosmetic: a bare
    /// "ConfigService.cs" also matches "IConfigService.cs".
    /// </summary>
    private static string ReadEmbedded(string suffix)
    {
        var normalized = suffix.Replace('/', '\\');
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
