using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The attention dot's AND mask has to be built from bits this code owns,
/// and the buffer has to be as long as the bitmap it describes.
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
/// The stride arithmetic is deliberately NOT checked here. It lives in
/// Ghostty.Core.Taskbar.MaskGeometry, where MaskGeometryTests executes it
/// over every width instead of reading it. This file only has to prove the
/// shell asks for that number and uses it whole.
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
        //
        // This does pin one implementation style. If CsWin32 ever grows a
        // span-taking CreateBitmap, the cleaner call with no `fixed` at all
        // reddens this, and the right response is to widen the rule rather
        // than to drop it.
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
    /// The buffer has to cover every byte GDI reads, and what GDI reads is
    /// decided by the first four arguments as much as by the length: a bit
    /// count of 8 in argument 3 makes it read eight times as much from the
    /// same pinned buffer. An undersized buffer is a worse fault than the
    /// undefined one it replaced, because the over-read happens inside a
    /// pinning region on every badge, so the geometry and the length are
    /// both held here.
    ///
    /// Everything below names identifiers rather than a spelling, so
    /// reformatting or swapping the operands stays green.
    /// </summary>
    [Fact]
    public void TheMaskBuffer_CoversEveryByteGdiReads()
    {
        var create = ShellSource.Load("Taskbar.AttentionOverlayIcon.cs").Method("Create");
        var call = create.Call("PInvoke.CreateBitmap");

        // Geometry: width, height, one plane, one bit per pixel. The stride
        // is only the right length for a 1-bpp single-plane bitmap, and
        // nothing about the buffer would catch a change here.
        Assert.Equal("w", call.Arg(0));
        Assert.Equal("h", call.Arg(1));
        Assert.Equal("1", call.Arg(2));
        Assert.Equal("1", call.Arg(3));

        // The call takes a pinned pointer; the buffer it is pinning is what
        // this test is actually about, so walk the fixed binding back to it.
        var pointer = Assert.IsType<IdentifierNameSyntax>(call.ArgExpression(4)).Identifier.ValueText;
        var binding = create.DescendantNodes().OfType<FixedStatementSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .SingleOrDefault(v => v.Identifier.ValueText == pointer);
        Assert.True(binding is not null, $"'{pointer}' is not bound by a fixed statement in Create()");
        var name = Assert.IsType<IdentifierNameSyntax>(binding!.Initializer!.Value).Identifier.ValueText;

        var declared = create.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == name)
            .ToList();
        Assert.True(declared.Count == 1, $"expected one declaration of '{name}', found {declared.Count}");

        var creation = Assert.IsType<ArrayCreationExpressionSyntax>(declared[0].Initializer!.Value);
        Assert.Equal("byte", creation.Type.ElementType.ToString());

        // new byte[N] is zero-filled by the runtime; an initializer list
        // would not be, for any length the metrics can return.
        Assert.Null(creation.Initializer);

        // ... and nothing writes to it afterwards. Without this, an
        // Array.Fill(maskBits, 0xFF) between the allocation and the pin
        // leaves every assertion here green while the mask stops being the
        // all-zero one Create() promises in its comment.
        var uses = create.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.ValueText == name)
            .ToList();
        Assert.True(
            uses.Count == 1 && uses[0] == binding.Initializer.Value,
            $"'{name}' is mentioned {uses.Count} time(s) in Create(); the only mention may be "
                + "the fixed binding, or something is rewriting the mask after it is zeroed.");

        // Length is the stride multiplied by the height and nothing else.
        // Naming both operands is what rules out `stride * h / 2`,
        // `stride * h - 1`, `stride * (h - 1)` and `stride + h`: each
        // mentions both identifiers, and each hands GDI a short buffer.
        var size = Assert.Single(creation.Type.RankSpecifiers).Sizes.Single();
        var product = Assert.IsType<BinaryExpressionSyntax>(size);
        Assert.True(
            product.IsKind(SyntaxKind.MultiplyExpression),
            $"mask buffer length is '{size}'; it has to be the stride multiplied by the height.");

        var operands = new[]
        {
            Assert.IsType<IdentifierNameSyntax>(product.Left).Identifier.ValueText,
            Assert.IsType<IdentifierNameSyntax>(product.Right).Identifier.ValueText,
        };
        Assert.True(
            operands.Contains("h"),
            $"mask buffer length multiplies '{operands[0]}' by '{operands[1]}'; "
                + "one of them has to be the bitmap height.");

        // The other operand has to be the stride that MaskGeometryTests
        // executes, not arithmetic spelled out here where nothing can
        // evaluate it. That test owns whether the number is right; this only
        // proves the shell asks for it, and asks for it about `w`.
        var strideName = operands[0] == "h" ? operands[1] : operands[0];
        var strideDecl = create.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(v => v.Identifier.ValueText == strideName);
        Assert.True(
            strideDecl?.Initializer is not null,
            $"'{strideName}' is not a local with an initializer in Create(), so the mask "
                + "buffer length is not the tested stride.");

        var strideCall = strideDecl!.Initializer!.Value.AssertCallTo("MaskGeometry.WordAlignedStride");
        Assert.Equal("w", strideCall.Arg(0));
    }
}
