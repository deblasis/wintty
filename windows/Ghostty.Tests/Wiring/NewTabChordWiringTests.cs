using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The Ctrl+Shift+T chord routing (#1187). The residual table is matched
/// before libghostty on both focus shapes (TerminalControl.KeyDown and the
/// frame chord router), and a hit is never forwarded, so any chord this
/// table claims is a chord the curated Config.zig defaults can never answer
/// for. Ctrl+Shift+T was claimed here for reopen-closed-tab while new_tab
/// moved to Ctrl+T: a Windows Terminal reflex opened nothing, and the chord
/// reported dispatched because reopen "ran" on an empty closed-tab store.
/// The chord is new_tab again; reopen lives on Ctrl+Shift+D.
///
/// The WinUI shell cannot load into this test host, so these facts are
/// pinned as parsed source. The libghostty half of the chord lives in
/// Config.zig's windows defaults and is pinned there by the zig-side
/// "keybind: windows default keybinds" test, which runs in the zig ladder.
/// </summary>
public class NewTabChordWiringTests
{
    // The residual table's entries, as written: (modifiers, key, action).
    private static readonly (string Modifiers, string Key, string Action)[] Table = LoadTable();

    private static (string Modifiers, string Key, string Action)[] LoadTable()
    {
        var property = ShellSource.Load("Input.KeyBindings.cs").Root
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "WindowsOnly");
        return property.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == "KeyBinding")
            .Select(o => (o.ArgumentList.Arguments[0].ToString(),
                          o.ArgumentList.Arguments[1].ToString(),
                          o.ArgumentList.Arguments[2].ToString()))
            .ToArray();
    }

    /// <summary>
    /// Non-vacuity: the sweep read the real table, not an empty one. A
    /// refactor that renames the record or the table would otherwise pass
    /// every assertion below against a sweep of nothing.
    /// </summary>
    [Fact]
    public void TheSweep_ReadTheResidualTable()
    {
        Assert.True(Table.Length >= 15,
            $"the residual sweep found {Table.Length} bindings; the table ships about 19");
    }

    /// <summary>
    /// The drop itself: no residual binding claims Ctrl+Shift+T. With the
    /// binding present this fails naming the claim, which is the chord
    /// #1187 pressed: dispatched=true, no tab, in every configuration
    /// shape, because reopen ran and had nothing to reopen.
    /// </summary>
    [Fact]
    public void TheNewTabChord_IsNotAResidualBinding()
    {
        var claim = Table.SingleOrDefault(b => b.Key == "VirtualKey.T");
        Assert.True(claim.Key is null,
            $"the residual table claims VirtualKey.T for {claim.Action}; Ctrl+Shift+T "
            + "is new_tab in the curated defaults, and a residual hit is never "
            + "forwarded to libghostty");
    }

    /// <summary>
    /// Reopen keeps its chord: Ctrl+Shift+D, Ctrl+Shift as exactly the two
    /// modifiers. Match() compares modifiers exactly, so a third modifier
    /// added here would make a chord no table answers.
    /// </summary>
    [Fact]
    public void ReopenClosedTab_IsBoundToCtrlShiftD()
    {
        var reopen = Table.Where(b => b.Action == "PaneAction.ReopenClosedTab").ToList();
        Assert.Single(reopen);
        Assert.Equal("VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift", reopen[0].Modifiers);
        Assert.Equal("VirtualKey.D", reopen[0].Key);
    }

    /// <summary>
    /// Nothing else claims D: the reopen binding must be the table's only
    /// VirtualKey.D, or Match returns the first hit and the other action is
    /// dead while reading as bound.
    /// </summary>
    [Fact]
    public void CtrlShiftD_IsClaimedOnce_ByReopen()
    {
        var claims = Table.Where(b => b.Key == "VirtualKey.D").ToList();
        Assert.Single(claims);
        Assert.Equal("PaneAction.ReopenClosedTab", claims[0].Action);
    }
}
