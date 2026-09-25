using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// Config.zig's windows defaults, pinned there by the zig ladder's
/// "keybind: windows default keybinds" test and textually by
/// WindowsDefaults_PinCtrlShiftTAsNewTab_Textually below, so the dotnet
/// inner loop alone still notices a dropped default.
/// </summary>
public class NewTabChordWiringTests
{
    // The residual table's entries, as written: (modifiers, key, action).
    // The Key slot is normalized so a numeric spelling like (VirtualKey)0x54
    // cannot walk through the string comparisons below.
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
                          NormalizeKey(o.ArgumentList.Arguments[1].ToString()),
                          o.ArgumentList.Arguments[2].ToString()))
            .ToArray();
    }

    // The table already spells OEM keys numerically ((VirtualKey)188 and
    // friends), so a respelled letter is in-style and would read as a plain
    // key to a human while dodging every comparison on "VirtualKey.T".
    private static string NormalizeKey(string written)
    {
        var m = Regex.Match(written, @"^\(VirtualKey\)\s*(0x[0-9A-Fa-f]+|\d+)$");
        if (!m.Success) return written;
        var digits = m.Groups[1].Value;
        var value = digits.StartsWith("0x", StringComparison.Ordinal)
            ? Convert.ToInt32(digits, 16)
            : Convert.ToInt32(digits);
        return $"VirtualKey.{(char)value}";
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
            $"the residual sweep found {Table.Length} bindings; the table ships 21");
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

    /// <summary>
    /// The sweep above reads only the WindowsOnly table; a second claiming
    /// site elsewhere in the shell would not appear in it. This walks every
    /// shell source the test host embeds and refuses any KeyBinding
    /// creation that claims the T key, whatever file it lives in.
    /// </summary>
    [Fact]
    public void NoShellSource_ClaimsTheTKey_Anywhere()
    {
        var keys = ShellSource.AllShellSources()
            .SelectMany(s => s.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            .Where(o => o.Type.ToString() == "KeyBinding" && o.ArgumentList.Arguments.Count >= 2)
            .Select(o => NormalizeKey(o.ArgumentList.Arguments[1].ToString()))
            .ToList();
        Assert.True(keys.Count >= Table.Length,
            $"the corpus sweep found {keys.Count} KeyBinding creations against a residual " +
            $"table of {Table.Length}; it read less than the whole shell");
        Assert.DoesNotContain("VirtualKey.T", keys);
    }

    /// <summary>
    /// The curated defaults keep ctrl+shift+t on new_tab in BOTH platform
    /// blocks that bind it (linux/bsd and windows). The zig ladder pins this
    /// at runtime; this is the dotnet-loop tripwire on the same embedded
    /// resource the tab-shell-verb scan reads, so a dropped or rebound
    /// default cannot ship green from a dotnet-only run (#1187).
    /// </summary>
    [Fact]
    public void WindowsDefaults_PinCtrlShiftTAsNewTab_Textually()
    {
        const string resource = "Ghostty.Tests.Config.Defaults.Config.zig";
        using var stream = typeof(ShellSource).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var config = reader.ReadToEnd();

        const string trigger =
            ".{ .key = .{ .unicode = 't' }, .mods = .{ .ctrl = true, .shift = true } },";
        var hits = 0;
        var idx = 0;
        while ((idx = config.IndexOf(trigger, idx, StringComparison.Ordinal)) >= 0)
        {
            var tail = config.Substring(idx, Math.Min(160, config.Length - idx));
            Assert.True(tail.Contains(".{ .new_tab = {} }", StringComparison.Ordinal),
                "a ctrl+shift+t default exists but is not new_tab; the apprt residual " +
                "table must not claim this chord (#1187)");
            hits++;
            idx += trigger.Length;
        }
        Assert.True(hits >= 2,
            $"expected the linux/bsd block and the windows block to each put " +
            $"ctrl+shift+t -> new_tab, found {hits}; if the zig default moved, " +
            "this text pin must move with it");
    }
}
