using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Ghostty.Core.Renderer;
using Xunit;

namespace Ghostty.Tests.Interop;

// Checks the managed FFI enums against include/ghostty.h itself, rather than
// against literals copied out of the same managed enum.
//
// The ordinals in GhosttyActionTag are positions in a C enum that upstream
// edits. Nothing about inserting a member there breaks the build: the header
// is not compiled here, the tags cross the boundary as plain ints, and every
// managed handler still compiles. The failure is a live misroute -- each tag
// after the insertion point is decoded as its predecessor -- and it shows up
// as unrelated misbehaviour rather than as an error.
//
// That happened: upstream added GHOSTTY_ACTION_SET_WINDOW_TITLE at 35, so
// CustomShaderFailed = 70 started receiving GHOSTTY_ACTION_FIRST_RENDER and
// every new surface raised a spurious "Custom shader not applied" notice.
// GhosttyActionsLayoutTests stayed green throughout, because it asserted
// (int)GhosttyActionTag.X == <literal copied from GhosttyActionTag.X>.
public class GhosttyActionTagHeaderParityTests
{
    private const string HeaderResource = "Ghostty.Tests.Interop.Header.ghostty.h";
    private const string ActionTypedef = "ghostty_action_tag_e";
    private const string ActionPrefix = "GHOSTTY_ACTION_";

    // Read once. The shift tests below re-read it a few hundred times.
    private static readonly Lazy<string> Header = new(LoadHeader);

    // A C enum entry, rejecting any explicit `= N`: the checks below map a
    // name to its position, which an explicit value would silently break.
    private static readonly Regex EntryPattern = new(
        @"^\s*(?<name>[A-Z][A-Z0-9_]*)\s*(?<assign>=)?[^,]*,?\s*(?://.*)?$",
        RegexOptions.Compiled);

    [Fact]
    public void ActionTag_Ordinals_Match_Header()
    {
        AssertMatchesHeader<GhosttyActionTag>(ActionTypedef, ActionPrefix);
    }

    // The payload enums. These never reach a switch on the managed side as a
    // named tag -- they arrive as a raw int the handler casts -- so a reorder
    // is silent in exactly the same way, just one level down.
    [Fact]
    public void SplitDirection_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttySplitDirection>(
            "ghostty_action_split_direction_e", "GHOSTTY_SPLIT_DIRECTION_");

    [Fact]
    public void GotoSplit_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyGotoSplit>(
            "ghostty_action_goto_split_e", "GHOSTTY_GOTO_SPLIT_");

    [Fact]
    public void GotoWindow_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyGotoWindow>(
            "ghostty_action_goto_window_e", "GHOSTTY_GOTO_WINDOW_");

    [Fact]
    public void ResizeSplitDirection_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyResizeSplitDirection>(
            "ghostty_action_resize_split_direction_e", "GHOSTTY_RESIZE_SPLIT_");

    [Fact]
    public void FloatWindow_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyFloatWindow>(
            "ghostty_action_float_window_e", "GHOSTTY_FLOAT_WINDOW_");

    [Fact]
    public void PromptTitle_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyPromptTitle>(
            "ghostty_action_prompt_title_e", "GHOSTTY_PROMPT_TITLE_");

    [Fact]
    public void ProgressState_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyProgressState>(
            "ghostty_action_progress_report_state_e", "GHOSTTY_PROGRESS_STATE_");

    [Fact]
    public void MouseShape_Ordinals_Match_Header() =>
        AssertMatchesHeader<MouseShape>(
            "ghostty_action_mouse_shape_e", "GHOSTTY_MOUSE_SHAPE_");

    [Fact]
    public void MouseVisibility_Ordinals_Match_Header() =>
        AssertMatchesHeader<MouseVisibility>(
            "ghostty_action_mouse_visibility_e", "GHOSTTY_MOUSE_");

    [Fact]
    public void CustomShaderFailure_Ordinals_Match_Header() =>
        AssertMatchesHeader<CustomShaderFailure>(
            "ghostty_action_custom_shader_failure_e", "GHOSTTY_CUSTOM_SHADER_FAILURE_");

    // Not covered: GhosttyGotoTab. Its header entries carry explicit negative
    // values, so position says nothing about them and the parser below refuses
    // the enum rather than pretending otherwise.

    // A check that goes red on a header it was handed is worth only as much as
    // its ability to go red on the header that actually broke us. These two
    // rebuild the shift that shipped -- an entry appearing in, or vanishing
    // from, the middle of the C enum -- at every position it could land, and
    // require the check above to name exactly the members it moves.
    //
    // "Exactly" is the assertion that matters. A check that flags the whole
    // enum whenever anything moves is not much better than no check: it points
    // at 45 tags when 20 drifted, and the reader has to redo the work. The
    // insertion at index k moves every member from k onward and nothing before
    // it, and that is what this holds it to.
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4242)]
    public void ActionTag_Check_Names_Exactly_The_Tags_An_Insertion_Moves(int seed) =>
        AssertShiftIsLocalized(seed, insert: true);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4242)]
    public void ActionTag_Check_Names_Exactly_The_Tags_A_Deletion_Moves(int seed) =>
        AssertShiftIsLocalized(seed, insert: false);

    private static void AssertShiftIsLocalized(int seed, bool insert)
    {
        var entryCount = ReadHeaderEnum(ActionTypedef).Count;
        var rng = new Random(seed);

        for (var i = 0; i < 12; i++)
        {
            var at = rng.Next(0, entryCount);
            var mutated = ShiftActionEnum(ReadHeader(), at, insert);

            var reported = FindMismatches<GhosttyActionTag>(mutated, ActionTypedef, ActionPrefix)
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            // Everything at or after the edit moves; everything before it does
            // not. A managed enum member is named iff its ordinal is in the
            // moved range -- the managed list is partial, so most positions
            // have no member and simply contribute nothing.
            var expected = Enum.GetValues<GhosttyActionTag>()
                .Where(v => (int)v >= at)
                .Select(v => v.ToString())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var what = insert ? "inserting an entry at" : "deleting the entry at";
            Assert.True(
                expected.SequenceEqual(reported),
                $"{what} {at} should have been reported as [{string.Join(", ", expected)}] " +
                $"but the check reported [{string.Join(", ", reported)}]");

            // The tail member is always in the moved range, so an edit anywhere
            // has to produce something. Belt and braces on the comparison above
            // going vacuous if both sides ever came back empty.
            Assert.NotEmpty(reported);
        }
    }

    // The managed enums are deliberately partial -- only the tags the Windows
    // apprt dispatches on are listed -- so this checks every managed member
    // against the header and not the reverse.
    private static void AssertMatchesHeader<TEnum>(string typedefName, string prefix)
        where TEnum : struct, Enum
    {
        var mismatches = FindMismatches<TEnum>(ReadHeader(), typedefName, prefix);

        Assert.True(
            mismatches.Count == 0,
            $"{typeof(TEnum).Name} has drifted from include/ghostty.h:\n  " +
            string.Join("\n  ", mismatches.Select(m => m.Detail)));
    }

    private static List<(string Name, string Detail)> FindMismatches<TEnum>(
        string headerText, string typedefName, string prefix)
        where TEnum : struct, Enum
    {
        var header = ParseHeaderEnum(headerText, typedefName);

        var mismatches = new List<(string Name, string Detail)>();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            var value = Convert.ToInt32(Enum.Parse<TEnum>(name));
            var cName = prefix + ToScreamingSnake(name);

            if (!header.TryGetValue(cName, out var expected))
            {
                mismatches.Add((name, $"{name} = {value}: {cName} is not in {typedefName}"));
                continue;
            }

            if (expected != value)
            {
                var actual = header.FirstOrDefault(kv => kv.Value == value).Key ?? "<out of range>";
                mismatches.Add((
                    name,
                    $"{name} = {value} but {cName} is {expected}; " +
                    $"{value} is now {actual}"));
            }
        }

        return mismatches;
    }

    // Returns the header with one entry added before, or removed from, position
    // `at` in the action enum -- the two ways an upstream edit renumbers the
    // tail. Only the action enum is rewritten; the rest of the file is copied
    // through untouched, so the parser is exercised on a real header.
    private static string ShiftActionEnum(string headerText, int at, bool insert)
    {
        var (open, close) = FindEnumBody(headerText, ActionTypedef);
        var body = headerText[open..close];

        var lines = body.Split('\n').ToList();
        var entryLines = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            entryLines.Add(i);
        }

        Assert.InRange(at, 0, entryLines.Count - 1);
        if (insert) lines.Insert(entryLines[at], "  GHOSTTY_ACTION_ZZ_FUZZ_INSERTED,");
        else lines.RemoveAt(entryLines[at]);

        return headerText[..open] + string.Join('\n', lines) + headerText[close..];
    }

    private static string ToScreamingSnake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(pascal[i]));
        }

        return sb.ToString();
    }

    // The character range of the body of `typedef enum { ... } <typedefName>;`,
    // exclusive of the braces. Located from the closing line backwards: the
    // opening `typedef enum {` is not unique in this header, so searching
    // forwards finds the first enum in the file and reads the wrong one.
    private static (int Open, int Close) FindEnumBody(string headerText, string typedefName)
    {
        var close = headerText.IndexOf("} " + typedefName + ";", StringComparison.Ordinal);
        Assert.True(close >= 0, $"{typedefName} not found in include/ghostty.h");

        var open = headerText.LastIndexOf("typedef enum {", close, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no enum body precedes {typedefName} in include/ghostty.h");

        return (open + "typedef enum {".Length, close);
    }

    private static Dictionary<string, int> ReadHeaderEnum(string typedefName) =>
        ParseHeaderEnum(ReadHeader(), typedefName);

    // Parses `typedef enum { ... } <typedefName>;` into name -> ordinal.
    private static Dictionary<string, int> ParseHeaderEnum(string headerText, string typedefName)
    {
        var (open, close) = FindEnumBody(headerText, typedefName);
        var body = headerText[open..close];

        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var m = EntryPattern.Match(line);
            Assert.True(m.Success, $"unparsed line in {typedefName}: {line}");

            // An explicit initializer would make position-based ordinals a lie.
            // ghostty.h uses them for negative sentinels (color kinds); if one
            // ever lands in an enum checked here, this must learn to read it.
            Assert.False(
                m.Groups["assign"].Success,
                $"{typedefName} entry has an explicit value, ordinals are no " +
                $"longer positional: {line}");

            entries[m.Groups["name"].Value] = index++;
        }

        // A parse that quietly yields nothing would pass every check above.
        Assert.True(
            entries.Count > 0,
            $"parsed no entries out of {typedefName}; the scan is broken, not the enum");

        return entries;
    }

    private static string ReadHeader() => Header.Value;

    private static string LoadHeader()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(HeaderResource);
        Assert.True(
            stream is not null,
            $"{HeaderResource} is not embedded; see Ghostty.Tests.csproj");

        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
