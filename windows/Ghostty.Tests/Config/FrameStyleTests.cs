using System;
using System.Linq;
using Ghostty.Core.Config;
using Ghostty.Core.Shell;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// <c>frame-style</c> gives the window chrome its own material, and an
/// unset key means "match the backdrop". That inheritance is resolved once,
/// at read time, so no consumer has to know it exists and a later one
/// cannot resolve it differently.
///
/// Half of what follows is behaviour over the fold the read goes through
/// and over the key registry; the other half is read off the parsed source.
/// <c>ConfigService</c> lives in the WinUI project, which this assembly
/// deliberately does not reference, so the read itself cannot be executed
/// here. The rule that most wants pinning is invisible to a behaviour test
/// anyway: the inheritance reads <c>BackgroundStyle</c>, so it has to run
/// after it. Reordered, it silently inherits the previous reload's value.
/// </summary>
public sealed class FrameStyleTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    private static MethodDeclarationSyntax ReadFlagsCore() => ConfigService().Method("ReadFlagsCore");

    private static AssignmentExpressionSyntax Assignment(MethodDeclarationSyntax method, string property) =>
        method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == property);

    private static ConditionalExpressionSyntax FrameStyleRead() =>
        Assert.IsType<ConditionalExpressionSyntax>(Assignment(ReadFlagsCore(), "FrameStyle").Right);

    /// <summary>
    /// Without this, libghostty parses the user's config, finds a key its
    /// Zig schema has never heard of, and the settings UI shows a config
    /// error for a setting the app honors.
    /// </summary>
    [Fact]
    public void The_key_is_registered_as_a_windows_only_key()
    {
        Assert.True(WindowsOnlyKeys.Contains("frame-style"));

        var entry = Assert.Single(WindowsOnlyKeys.All, e => e.Key == "frame-style");
        Assert.Contains("background-style", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_FrameStyle_property_starts_at_the_shared_default()
    {
        var property = ConfigService().Root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "FrameStyle");

        Assert.NotNull(property.Initializer);
        Assert.Equal("BackdropStyles.Default", property.Initializer!.Value.ToString());
    }

    /// <summary>
    /// The presence test is the whole feature. <c>GetFileValue</c> takes a
    /// default and so cannot tell an absent key from one configured to the
    /// same value, which is exactly the distinction "match the backdrop"
    /// is made of.
    /// </summary>
    [Fact]
    public void The_read_asks_whether_the_key_is_there_at_all()
    {
        var condition = FrameStyleRead().Condition.AssertCallTo("TryGetFileValue");
        Assert.Equal("\"frame-style\"", condition.Arg(0));

        var sentinel = ConfigService().Root.Calls("GetFileValue")
            .Where(c => c.Arg(0) == "\"frame-style\"")
            .ToList();
        Assert.True(
            sentinel.Count == 0,
            "frame-style is read through GetFileValue, whose default is indistinguishable "
                + "from a configured value equal to it");
    }

    /// <summary>
    /// A second lookup path is a second answer to "is this key set", and
    /// the two drift the first time the cache changes shape.
    /// </summary>
    [Fact]
    public void TryGetFileValue_reads_the_same_cache_as_GetFileValue()
    {
        var method = ConfigService().Method("TryGetFileValue");

        Assert.Contains(
            method.DescendantNodes().OfType<IdentifierNameSyntax>(),
            id => id.Identifier.ValueText == "_configFileCache");

        Assert.True(
            method.Calls("GetFileValue").Count == 0,
            "TryGetFileValue is layered over GetFileValue, so it can only report presence "
                + "by comparing against a sentinel");

        var value = Assert.Single(
            method.ParameterList.Parameters,
            p => p.Identifier.ValueText == "value");
        Assert.Contains(value.Modifiers, m => m.ToString() == "out");
    }

    [Fact]
    public void An_unset_frame_style_inherits_the_backdrop()
    {
        Assert.Equal("BackgroundStyle", FrameStyleRead().WhenFalse.ToString());
    }

    /// <summary>
    /// The inherited value is whatever <c>background-style</c> resolved to,
    /// which for an unusable one is the shared fallback rather than the
    /// user's typo. That only holds if this read runs after it.
    /// </summary>
    [Fact]
    public void The_inherited_value_is_read_after_background_style_resolves()
    {
        var method = ReadFlagsCore();

        Assert.True(
            Assignment(method, "BackgroundStyle").SpanStart < Assignment(method, "FrameStyle").SpanStart,
            "FrameStyle is resolved before BackgroundStyle, so an unset frame-style inherits "
                + "the value left over from the previous reload");
    }

    [Fact]
    public void A_configured_frame_style_wins_and_folds_through_the_same_normaliser()
    {
        var read = FrameStyleRead();

        var bound = Assert.IsType<DeclarationExpressionSyntax>(
            read.Condition.AssertCallTo("TryGetFileValue").ArgExpression(1));
        var raw = Assert.IsType<SingleVariableDesignationSyntax>(bound.Designation)
            .Identifier.ValueText;

        var fold = read.WhenTrue.AssertCallTo("NormalizeStyle");
        Assert.Equal("\"frame-style\"", fold.Arg(0));
        Assert.Equal(raw, fold.Arg(1));
    }

    /// <summary>
    /// A misspelled frame-style that quietly picked up the backdrop's value
    /// is indistinguishable from the misspelling working, so it lands on
    /// the shared default and gets reported instead.
    /// </summary>
    [Fact]
    public void An_unusable_frame_style_falls_back_instead_of_inheriting()
    {
        Assert.False(BackdropStyles.TryNormalize("mica", out var style));
        Assert.Equal(BackdropStyles.Default, style);

        Assert.DoesNotContain(
            "BackgroundStyle",
            FrameStyleRead().WhenTrue.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Setting frame-style to what background-style already says has to
    /// change nothing, including when the two are spelled differently: both
    /// arms hand back a value that has been through the same fold.
    /// </summary>
    [Fact]
    public void A_frame_style_equal_to_the_backdrop_resolves_to_the_same_value()
    {
        Assert.True(BackdropStyles.TryNormalize("frosted", out var backdrop));
        Assert.True(BackdropStyles.TryNormalize("  FROSTED ", out var frame));
        Assert.Equal(backdrop, frame);
    }
}
