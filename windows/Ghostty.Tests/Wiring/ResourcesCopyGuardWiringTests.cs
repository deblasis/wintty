using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Wiring;

// Pins the wiring of the resources copy guards in Ghostty.csproj: the two
// ErrorIfResources targets, the staging facts they compare against, and the
// copy items the whole mechanism exists to protect.
//
// The guards themselves run at build time and read the staged zig-out tree,
// which this test project never has. What they cannot check from a test is
// their own presence and shape: deleting a guard, conditioning it away,
// defusing its Errors into Messages, or reverting it to the pre-fix two-path
// Exists form leaves every build green on a healthy tree, which is exactly
// how the guarded items were lost the first time. These tests are the pin
// for that class of edit.
//
// The csproj arrives as an embedded resource captured when Ghostty.Tests
// builds, not read from disk at run time, so a csproj edit that does not
// rebuild this project shows the tests a stale copy (a restore that leaves
// the csproj with an older write time skips the re-embed). If a result here
// contradicts the file on disk, rebuild before believing it.
public class ResourcesCopyGuardWiringTests
{
    // Ghostty.csproj, embedded by Ghostty.Tests.csproj. Read as text: this
    // project deliberately does not reference Ghostty.csproj, which would
    // drag the WinAppSDK MRT/PRI targets into a plain net10.0 assembly.
    private const string ProjectResourceName = "Ghostty.Tests.Shell.Ghostty.csproj";

    private static readonly Regex CommentRegex = new(@"(?s)<!--.*?-->", RegexOptions.Compiled);

    private static string ProjectWithoutComments()
    {
        var project = ReadEmbeddedText(ProjectResourceName);
        return CommentRegex.Replace(project, string.Empty);
    }

    /// <summary>
    /// The named target's whole block, comments stripped so a target that
    /// only survives inside a comment does not pass for a wired one.
    /// </summary>
    private static string TargetBlock(string project, string name)
    {
        var start = project.IndexOf($"<Target Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Ghostty.csproj has no <Target Name=\"{name}\"> outside comments.");
        var end = project.IndexOf("</Target>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Ghostty.csproj: target {name} has no closing tag.");
        return project.Substring(start, end + "</Target>".Length - start);
    }

    /// <summary>
    /// The target's opening tag, where the wiring attributes live.
    /// </summary>
    private static string OpeningTag(string block)
    {
        var end = block.IndexOf('>');
        return block.Substring(0, end + 1);
    }

    private static string NoneElement(string project, string includePath)
    {
        var match = Regex.Match(
            project,
            @"(?s)<None Include=""" + Regex.Escape(includePath) + @"""" + @"[^>]*>.*?</None>");
        Assert.True(
            match.Success,
            $"Ghostty.csproj: expected a <None Include=\"{includePath}\"> copy item, found none.");
        return match.Value;
    }

    /// <summary>
    /// The condition only bites because it sits on an Error task: the same
    /// string on a Message task logs and the build passes. Each pinned
    /// condition is asserted inside an Error's opening tag, not just
    /// anywhere in the target.
    /// </summary>
    private static void AssertFatalCondition(string block, string condition)
    {
        Assert.True(
            Regex.IsMatch(block, @"<Error\b[^>]*" + Regex.Escape(condition)),
            $"Ghostty.csproj: the guard condition {condition} must sit on an <Error> task, " +
            "not a <Message>: as a Message it logs and the build passes.");
    }

    // The pre-fix guard also hooked AfterTargets="Build" and also carried no
    // Condition; what it did not do was derive the expected set from the
    // staged tree. Reverting to it (or deleting the staging dependency)
    // passes this test's target-shape checks and fails only here, so this is
    // the assertion that owns the wholesale revert.
    [Fact]
    public void OutputGuardComparesOutputAgainstTheStagedTree()
    {
        var block = TargetBlock(ProjectWithoutComments(), "ErrorIfResourcesNotInOutput");

        AssertFatalCondition(
            block,
            "!Exists('$(OutDir)%(_ExpectedResourceDest.Identity)')");
        AssertFatalCondition(block, "_StagedWithoutCopyItem.Identity");
        AssertFatalCondition(block, "_CopyItemWithoutStage.Identity");
    }

    [Fact]
    public void OutputGuardRunsOnEveryBuild()
    {
        var tag = OpeningTag(TargetBlock(ProjectWithoutComments(), "ErrorIfResourcesNotInOutput"));

        Assert.Contains("AfterTargets=\"Build\"", tag, StringComparison.Ordinal);
        Assert.Contains(
            "DependsOnTargets=\"_StageResourcesGuardFacts\"",
            tag,
            StringComparison.Ordinal);
        // A Condition here is how the guard gets skipped: every skip shape
        // ever seen is an attribute on the opening tag.
        Assert.DoesNotContain("Condition=", tag, StringComparison.Ordinal);
    }

    // The publish folder is the one that ships, and a no-build publish never
    // runs the Build hook, so the publish half has to be pinned on its own.
    [Fact]
    public void PublishGuardComparesPublishFolderAgainstTheStagedTree()
    {
        var block = TargetBlock(ProjectWithoutComments(), "ErrorIfResourcesNotInPublish");

        AssertFatalCondition(
            block,
            "!Exists('$(PublishDir)%(_ExpectedResourceDest.Identity)')");
        AssertFatalCondition(block, "_StagedWithoutCopyItem.Identity");
        AssertFatalCondition(block, "_CopyItemWithoutStage.Identity");
    }

    [Fact]
    public void PublishGuardRunsOnEveryPublish()
    {
        var tag = OpeningTag(TargetBlock(ProjectWithoutComments(), "ErrorIfResourcesNotInPublish"));

        Assert.Contains("AfterTargets=\"Publish\"", tag, StringComparison.Ordinal);
        Assert.Contains(
            "DependsOnTargets=\"_StageResourcesGuardFacts\"",
            tag,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Condition=", tag, StringComparison.Ordinal);
    }

    // With no staged zig-out there is nothing to compare and a green exit
    // would mean unchecked. The fail-closed Error is what separates an
    // honest guard from one that skips silently on a bare tree.
    [Fact]
    public void BothGuardsFailClosedOnAnUnstagedTree()
    {
        var project = ProjectWithoutComments();

        foreach (var name in new[] { "ErrorIfResourcesNotInOutput", "ErrorIfResourcesNotInPublish" })
        {
            AssertFatalCondition(
                TargetBlock(project, name),
                "!Exists('..\\..\\zig-out\\lib\\ghostty.dll')");
        }
    }

    // The staging globs define what "the whole tree" means. Narrow one (say
    // to a single shell's subdirectory) and the guard happily blesses the
    // narrowed set, so the globs are pinned exactly.
    [Fact]
    public void StagingReadsEverySurfaceOfTheZigOutTree()
    {
        var block = TargetBlock(ProjectWithoutComments(), "_StageResourcesGuardFacts");

        Assert.Contains(
            "<_ZigOutShellScript Include=\"..\\..\\zig-out\\share\\ghostty\\shell-integration\\**\\*\"",
            block,
            StringComparison.Ordinal);
        Assert.Contains(
            "<_ZigOutTerminfo Include=\"..\\..\\zig-out\\share\\terminfo\\ghostty.terminfo\"",
            block,
            StringComparison.Ordinal);
        // A literal Include creates the item even when the file is absent,
        // so the literal has to gate itself: without the Exists condition a
        // partially staged tree drifts and the guard claims a file is staged
        // that nobody read. The dll literal is exempt; the fail-closed
        // precheck owns its absence.
        Assert.Contains(
            "Condition=\"Exists('..\\..\\zig-out\\share\\terminfo\\ghostty.terminfo')\"",
            block,
            StringComparison.Ordinal);
        Assert.Contains(
            "<_ZigOutTheme Include=\"..\\..\\zig-out\\share\\ghostty\\themes\\*\"",
            block,
            StringComparison.Ordinal);
        Assert.Contains(
            "<_ZigOutDll Include=\"..\\..\\zig-out\\lib\\ghostty.dll\"",
            block,
            StringComparison.Ordinal);
        Assert.Contains(
            "<_ZigOutPdb Include=\"..\\..\\zig-out\\lib\\ghostty.pdb\"",
            block,
            StringComparison.Ordinal);
    }

    // The copy items are what the guards exist to protect, and a tree with
    // nothing staged cannot redden a build that deletes one. Pinning them
    // here keeps the deletion red everywhere, including from this test
    // project, which never stages zig-out at all.
    [Fact]
    public void CopyItemsShipEverySurface()
    {
        var project = ProjectWithoutComments();

        var dll = NoneElement(project, @"..\..\zig-out\lib\ghostty.dll");
        Assert.Contains("<Link>native\\ghostty.dll</Link>", dll, StringComparison.Ordinal);
        Assert.Contains(
            "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
            dll,
            StringComparison.Ordinal);

        var pdb = NoneElement(project, @"..\..\zig-out\lib\ghostty.pdb");
        Assert.Contains("<Link>native\\ghostty.pdb</Link>", pdb, StringComparison.Ordinal);
        Assert.Contains(
            "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
            pdb,
            StringComparison.Ordinal);

        var themes = NoneElement(project, @"..\..\zig-out\share\ghostty\themes\*");
        Assert.Contains(
            "<Link>share\\ghostty\\themes\\%(Filename)%(Extension)</Link>",
            themes,
            StringComparison.Ordinal);
        Assert.Contains(
            "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
            themes,
            StringComparison.Ordinal);

        var scripts = NoneElement(
            project,
            @"..\..\zig-out\share\ghostty\shell-integration\**\*");
        Assert.Contains(
            "<Link>share\\ghostty\\shell-integration\\%(RecursiveDir)%(Filename)%(Extension)</Link>",
            scripts,
            StringComparison.Ordinal);
        Assert.Contains(
            "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
            scripts,
            StringComparison.Ordinal);

        var terminfo = NoneElement(project, @"..\..\zig-out\share\terminfo\ghostty.terminfo");
        Assert.Contains(
            "<Link>share\\terminfo\\ghostty.terminfo</Link>",
            terminfo,
            StringComparison.Ordinal);
        Assert.Contains(
            "<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
            terminfo,
            StringComparison.Ordinal);
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
