using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The attention dot's AND mask has to be built from bits this code owns.
///
/// CreateBitmap allocates the plane but leaves its contents undefined when
/// lpvBits is null, so passing null makes "an all-zero mask" a claim about
/// uninitialized memory. On Windows 11 the mask turns out to be inert for
/// this icon -- a 32-bpp color plane with a real alpha channel composites
/// from the alpha and the mask is never consulted, so a deliberately
/// all-ones mask renders pixel-for-pixel the same dot through
/// ITaskbarList3::SetOverlayIcon. That is what makes this a guard rather
/// than a regression test: there is no rendering assertion to make, and the
/// only thing worth holding is the shape of the call.
///
/// A wiring guard, in the sense ShellSource describes: the shell assembly
/// is not loadable here, so this reads the source. What GDI then does with
/// the bits is only observable on a live desktop.
/// </summary>
public sealed class AttentionOverlayIconWiringTests
{
    [Fact]
    public void TheMaskBitmap_IsBuiltFromBitsTheCallerOwns()
    {
        var create = ShellSource.Load("Taskbar.AttentionOverlayIcon.cs").Method("Create");
        var bits = create.Call("PInvoke.CreateBitmap").ArgExpression(4);

        Assert.False(
            bits.IsKind(SyntaxKind.NullLiteralExpression)
                || bits.IsKind(SyntaxKind.DefaultLiteralExpression),
            "CreateBitmap leaves the bitmap contents undefined when lpvBits is null. "
                + "The overlay mask has to be handed bits, not left to whatever the "
                + "allocation happened to contain.");

        // Not just non-null: the pointer has to be a buffer pinned here, so
        // that a future edit cannot satisfy the rule above with a pointer
        // into memory this method never wrote.
        var name = Assert.IsType<IdentifierNameSyntax>(bits).Identifier.ValueText;
        var pinned = create.DescendantNodes().OfType<FixedStatementSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Any(v => v.Identifier.ValueText == name);
        Assert.True(
            pinned,
            $"'{name}' reaches CreateBitmap without being pinned by a fixed statement "
                + "in Create(); the mask bits would not be a buffer this method owns.");
    }

    /// <summary>
    /// The buffer has to cover the whole mask, or CreateBitmap reads past it.
    /// 1-bpp GDI scanlines are WORD aligned, so the stride is
    /// ((w + 15) / 16) * 2 and the buffer is that times the height. An
    /// undersized buffer is a worse bug than the undefined one it replaced,
    /// so the arithmetic is held in place rather than left to a reviewer.
    /// </summary>
    [Fact]
    public void TheMaskBuffer_IsWordAlignedStrideTimesHeight()
    {
        var create = ShellSource.Load("Taskbar.AttentionOverlayIcon.cs").Method("Create");
        var pointer = Assert.IsType<IdentifierNameSyntax>(
            create.Call("PInvoke.CreateBitmap").ArgExpression(4)).Identifier.ValueText;

        // The call takes a pinned pointer; the buffer it is pinning is what
        // this test is actually about, so walk the fixed binding back to it.
        var binding = create.DescendantNodes().OfType<FixedStatementSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Single(v => v.Identifier.ValueText == pointer);
        var name = Assert.IsType<IdentifierNameSyntax>(binding.Initializer!.Value).Identifier.ValueText;

        var declared = create.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == name)
            .ToList();
        Assert.True(declared.Count == 1, $"expected one declaration of '{name}', found {declared.Count}");

        var creation = Assert.IsType<ArrayCreationExpressionSyntax>(declared[0].Initializer!.Value);
        Assert.Equal("byte", creation.Type.ElementType.ToString());

        // new byte[N] is zero-filled by the runtime; an initializer list
        // would not be, for any length the metrics can return.
        Assert.Null(creation.Initializer);

        // The stride itself has to be the WORD-aligned one. Matched on the
        // expression because no scan can evaluate it, but only to find the
        // declaration -- the length assertion below names identifiers rather
        // than a spelling, so reordering the operands stays green.
        var strideName = create.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Initializer is not null
                         && v.Initializer.Value.ToString().Replace(" ", "") == "((w+15)/16)*2")
            .Identifier.ValueText;

        var size = Assert.Single(creation.Type.RankSpecifiers).Sizes.Single();
        var mentioned = size.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.ValueText)
            .ToList();
        Assert.Contains(strideName, mentioned);
        Assert.Contains("h", mentioned);
    }
}
