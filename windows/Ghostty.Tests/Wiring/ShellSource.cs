using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// One WinUI source file, parsed.
///
/// The shell assembly cannot be loaded into a test host, so a test that
/// wants to know whether a handler still calls something has only the
/// source to go on. Parsing it rather than searching it is the whole
/// point: a mutation that keeps a literal and drops the behaviour (an
/// `if (false && ...)`, a method rewritten to `return null;`) walks
/// straight through a substring match and changes the syntax tree.
///
/// These are wiring guards, not behaviour tests. They prove a call site
/// is still shaped the way the fix left it. Whether a screen reader then
/// speaks is only observable on a live UIA tree.
/// </summary>
internal sealed class ShellSource
{
    private readonly CompilationUnitSyntax _root;

    private ShellSource(CompilationUnitSyntax root) => _root = root;

    /// <summary>
    /// Load an embedded shell source by its dotted path tail, e.g.
    /// "Controls.CommandPalette.CommandPaletteControl.xaml.cs".
    ///
    /// The tail must include enough directories to be unique: the
    /// resource names keep the source's folders, and a bare file name
    /// would start matching two files the day a second MainWindow.xaml.cs
    /// or TerminalControl.xaml.cs appears somewhere else in the tree.
    /// </summary>
    public static ShellSource Load(string dottedTail)
    {
        var asm = Assembly.GetExecutingAssembly();
        var matches = asm.GetManifestResourceNames()
            .Where(n => Normalize(n).EndsWith("." + dottedTail, StringComparison.Ordinal))
            .ToList();
        Assert.True(
            matches.Count == 1,
            $"expected exactly one embedded source ending in '{dottedTail}', found {matches.Count}: "
                + string.Join(", ", matches));

        using var stream = asm.GetManifestResourceStream(matches[0])!;
        using var reader = new StreamReader(stream);
        // Conditional regions are invisible with default parse options:
        // Roslyn keeps them as disabled trivia, so anything inside `#if DEMO`
        // is simply absent from the tree. That is not a gap a census can live
        // with, because it reports full coverage of a file it only partly
        // read, and DEMO is a configuration that ships.
        //
        // Defining the symbol reveals that branch. It cannot be the whole
        // answer, because it hides the other one: an `#else` or `#if !DEMO`
        // becomes the disabled region instead. So the assertion below refuses
        // any tree that still has disabled text, which turns adding a new
        // conditional into a decision someone has to make rather than a
        // silent widening of the blind spot.
        var options = new CSharpParseOptions(preprocessorSymbols: new[] { "DEMO" });
        var tree = CSharpSyntaxTree.ParseText(reader.ReadToEnd(), options);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var hidden = root.DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia))
            .Select(t => tree.GetLineSpan(t.Span).StartLinePosition.Line + 1)
            .ToList();
        Assert.True(
            hidden.Count == 0,
            $"{dottedTail} has conditional regions this parse cannot see, at line(s) "
                + string.Join(", ", hidden)
                + ". Every scan over this file silently skips them. Either define the "
                + "symbol that enables them here, or restructure so the code is not "
                + "hidden from the wiring tests.");

        return new ShellSource(root);
    }

    // MSBuild substitutes the host's directory separator into the logical
    // name, so neither "\" nor "/" can be assumed. Fold both to dots.
    private static string Normalize(string resourceName) =>
        resourceName.Replace('\\', '.').Replace('/', '.');

    /// <summary>The whole parsed file.</summary>
    public SyntaxNode Root => _root;

    /// <summary>The one method with this name in the file.</summary>
    public MethodDeclarationSyntax Method(string name)
    {
        var found = _root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == name)
            .ToList();
        Assert.True(found.Count == 1, $"expected one method named '{name}', found {found.Count}");
        return found[0];
    }

    /// <summary>The one field with this name in the file.</summary>
    public (VariableDeclaratorSyntax Variable, FieldDeclarationSyntax Field) Field(string name)
    {
        var found = _root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => (Variable: v, Field: f)))
            .Where(x => x.Variable.Identifier.ValueText == name)
            .ToList();
        Assert.True(found.Count == 1, $"expected one field named '{name}', found {found.Count}");
        return found[0];
    }

    /// <summary>
    /// The switch section inside <paramref name="method"/> whose labels
    /// mention <paramref name="label"/>.
    /// </summary>
    public SwitchSectionSyntax Case(string method, string label)
    {
        var found = Method(method).DescendantNodes().OfType<SwitchSectionSyntax>()
            .Where(s => s.Labels.ToString().Contains(label, StringComparison.Ordinal))
            .ToList();
        Assert.True(found.Count == 1, $"expected one '{label}' case in {method}, found {found.Count}");
        return found[0];
    }
}

internal static class SyntaxQueries
{
    /// <summary>
    /// Every call to <paramref name="target"/> under <paramref name="node"/>,
    /// matched on the parsed callee expression rather than on text: a call
    /// that is present only inside a comment or a string is not one.
    /// </summary>
    public static List<InvocationExpressionSyntax> Calls(this SyntaxNode node, string target) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == target)
            .ToList();

    /// <summary>
    /// The callee the way the source spells it, receiver included. A
    /// null-conditional call parses with the receiver hoisted out, so the
    /// callee on its own reads as ".TryEnqueue"; put the receiver back so a
    /// test can name the call as written. Keeping the receiver is what lets a
    /// test tell `_switchStoryboard.Stop` from a Stop on anything else.
    /// </summary>
    public static string CalleeText(this InvocationExpressionSyntax call)
    {
        if (call.Expression is MemberBindingExpressionSyntax
            && call.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } conditional)
        {
            return conditional.Expression + "?" + call.Expression;
        }
        return call.Expression.ToString();
    }

    /// <summary>The one call to <paramref name="target"/>.</summary>
    public static InvocationExpressionSyntax Call(this SyntaxNode node, string target)
    {
        var found = node.Calls(target);
        Assert.True(found.Count == 1, $"expected one call to '{target}', found {found.Count}");
        return found[0];
    }

    /// <summary>The literal source of one argument of a call.</summary>
    public static string Arg(this InvocationExpressionSyntax call, int index)
    {
        Assert.True(
            call.ArgumentList.Arguments.Count > index,
            $"'{call.Expression}' has {call.ArgumentList.Arguments.Count} arguments, wanted index {index}");
        return call.ArgumentList.Arguments[index].ToString();
    }
}
