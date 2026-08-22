using System;
using System.Collections.Generic;
using System.Globalization;
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

    // A C enum entry. The value group is optional so both spellings parse, but
    // which one is ALLOWED is decided per enum below: mixing them is what would
    // make a positional read wrong. A value is a plain integer or the `1 << N`
    // the header writes bit flags as. Anything else -- two entries on a line, a
    // wider expression, a block comment -- fails to match and asserts.
    private static readonly Regex EntryPattern = new(
        @"^\s*(?<name>[A-Z][A-Z0-9_]*)\s*(?:=\s*(?<value>-?\d+|1\s*<<\s*\d+))?\s*,?\s*(?://.*)?$",
        RegexOptions.Compiled);

    // The tag enum is a deliberately partial mirror: only the tags the Windows
    // apprt dispatches on are listed, so a header member with no managed
    // counterpart is expected and says nothing.
    [Fact]
    public void ActionTag_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyActionTag>(ActionTypedef, ActionPrefix, complete: false);

    // The payload enums. These never reach a switch on the managed side as a
    // named tag -- they arrive as a raw int the handler casts -- so a reorder
    // is silent in exactly the same way, just one level down.
    //
    // They are checked in BOTH directions, unlike the tag enum. A payload enum
    // is a closed set the handler has to be able to tell apart, so a member
    // upstream APPENDS is a live behaviour gap rather than a tag we chose not
    // to dispatch: nothing renumbers, nothing breaks, and the new value falls
    // into whichever branch the handler happens to end with. That is not
    // hypothetical -- the sync that shifted the tags above also appended
    // GHOSTTY_PROMPT_TITLE_WINDOW, and a one-directional check saw nothing.
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

    // GhosttyGotoTab is the one enum here whose header entries carry explicit
    // values, and they are negative sentinels rather than positions. Read with
    // `explicitValues`, which parses `= N` instead of counting: refusing it
    // outright left the only ABI enum with hand-written values as the only one
    // nothing checked.
    [Fact]
    public void GotoTab_Values_Match_Header() =>
        AssertMatchesHeader<GhosttyGotoTab>(
            "ghostty_action_goto_tab_e", "GHOSTTY_GOTO_TAB_", explicitValues: true);

    // The enums that are not action payloads: what a surface is told about the
    // platform it runs on, what the clipboard call is for, what the mouse and
    // keyboard did, and how a point is addressed. They lived beside the
    // P/Invokes in the WinUI project, which this assembly cannot reference, so
    // until they moved to Ghostty.Core nothing could check any of them.
    [Fact]
    public void Platform_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyPlatform>("ghostty_platform_e", "GHOSTTY_PLATFORM_");

    [Fact]
    public void SurfaceContext_Values_Match_Header() =>
        AssertMatchesHeader<GhosttySurfaceContext>(
            "ghostty_surface_context_e", "GHOSTTY_SURFACE_CONTEXT_", explicitValues: true);

    [Fact]
    public void Clipboard_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyClipboard>("ghostty_clipboard_e", "GHOSTTY_CLIPBOARD_");

    [Fact]
    public void ClipboardRequest_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyClipboardRequest>(
            "ghostty_clipboard_request_e", "GHOSTTY_CLIPBOARD_REQUEST_");

    [Fact]
    public void MouseState_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyMouseState>("ghostty_input_mouse_state_e", "GHOSTTY_MOUSE_");

    [Fact]
    public void MouseButton_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyMouseButton>("ghostty_input_mouse_button_e", "GHOSTTY_MOUSE_");

    [Fact]
    public void ColorScheme_Values_Match_Header() =>
        AssertMatchesHeader<GhosttyColorScheme>(
            "ghostty_color_scheme_e", "GHOSTTY_COLOR_SCHEME_", explicitValues: true);

    // Bit flags, like the binding flags above.
    [Fact]
    public void Mods_Values_Match_Header() =>
        AssertMatchesHeader<GhosttyMods>(
            "ghostty_input_mods_e", "GHOSTTY_MODS_", explicitValues: true);

    [Fact]
    public void InputAction_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyInputAction>("ghostty_input_action_e", "GHOSTTY_ACTION_");

    [Fact]
    public void PointTag_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyPointTag>("ghostty_point_tag_e", "GHOSTTY_POINT_");

    [Fact]
    public void PointCoord_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyPointCoord>("ghostty_point_coord_e", "GHOSTTY_POINT_COORD_");

    // ghostty_target_tag_e decides which half of GhosttyHost.OnAction an action
    // is delivered to, so swapping these two routes every app action into the
    // surface arm. It used to be two consts beside that switch, where no test
    // could see it.
    [Fact]
    public void TargetTag_Ordinals_Match_Header() =>
        AssertMatchesHeader<GhosttyTargetTag>("ghostty_target_tag_e", "GHOSTTY_TARGET_");

    // Bit flags rather than positions, so read as explicit values. The managed
    // None = 0 is skipped as a [Flags] convention; everything else has to be
    // the bit the header names.
    [Fact]
    public void BindingFlags_Values_Match_Header() =>
        AssertMatchesHeader<GhosttyBindingFlags>(
            "ghostty_binding_flags_e", "GHOSTTY_BINDING_FLAGS_", explicitValues: true);

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
    [Fact]
    public void ActionTag_Check_Names_Exactly_The_Tags_An_Insertion_Moves() =>
        AssertShiftIsLocalized(insert: true);

    [Fact]
    public void ActionTag_Check_Names_Exactly_The_Tags_A_Deletion_Moves() =>
        AssertShiftIsLocalized(insert: false);

    private static void AssertShiftIsLocalized(bool insert)
    {
        var entryCount = ReadHeaderEnum(ActionTypedef).Count;

        // Every position, not a sample. The invariant does not vary with `at`,
        // so a seeded draw would only cost coverage: the interesting positions
        // are the first and last entry lines, where the body slice carries its
        // empty leading and trailing lines, and a sample can miss them.
        for (var at = 0; at < entryCount; at++)
        {
            var mutated = ShiftActionEnum(ReadHeader(), at, insert);

            // complete:false, as for the real check. The synthetic entry has no
            // managed counterpart by construction, and counting that as drift
            // would swamp the localization this is measuring.
            var reported = FindMismatches<GhosttyActionTag>(
                    mutated, ActionTypedef, ActionPrefix, complete: false)
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

    private static void AssertMatchesHeader<TEnum>(
        string typedefName, string prefix, bool complete = true, bool explicitValues = false)
        where TEnum : struct, Enum
    {
        var mismatches = FindMismatches<TEnum>(
            ReadHeader(), typedefName, prefix, complete, explicitValues);

        Assert.True(
            mismatches.Count == 0,
            $"{typeof(TEnum).Name} has drifted from include/ghostty.h:\n  " +
            string.Join("\n  ", mismatches.Select(m => m.Detail)));
    }

    private static List<(string Name, string Detail)> FindMismatches<TEnum>(
        string headerText, string typedefName, string prefix,
        bool complete = true, bool explicitValues = false)
        where TEnum : struct, Enum
    {
        var header = ParseHeaderEnum(headerText, typedefName, explicitValues);

        // A [Flags] enum's zero member is sometimes the "no bits" convention C
        // does not write down. Sometimes it is, though -- ghostty_input_mods_e
        // spells GHOSTTY_MODS_NONE -- so this is a fallback for a zero the
        // header does not name, not a blanket exemption. Skipping every flags
        // zero would stop the completeness check seeing a NONE that vanished.
        var isFlags = typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false);

        var mismatches = new List<(string Name, string Detail)>();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            var value = Convert.ToInt32(Enum.Parse<TEnum>(name));
            var cName = prefix + ToScreamingSnake(name);

            if (!header.TryGetValue(cName, out var expected))
            {
                if (isFlags && value == 0) continue;
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

        if (complete)
        {
            var mirrored = Enum.GetNames<TEnum>()
                .Select(n => prefix + ToScreamingSnake(n))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (cName, value) in header.OrderBy(kv => kv.Value))
            {
                if (mirrored.Contains(cName)) continue;
                mismatches.Add((
                    cName,
                    $"{cName} = {value} has no member in {typeof(TEnum).Name}; the handler " +
                    $"cannot tell it from whichever branch it falls into"));
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

        // Entry lines identified with EntryPattern rather than by a second
        // "non-blank and not a comment" rule, so this cannot come to disagree
        // with the parser about what an entry is.
        var lines = body.Split('\n').ToList();
        var entryLines = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (EntryPattern.Match(lines[i].Trim()) is { Success: true } m && m.Groups["name"].Success)
                entryLines.Add(i);
        }

        Assert.InRange(at, 0, entryLines.Count - 1);
        if (insert) lines.Insert(entryLines[at], "  GHOSTTY_ACTION_ZZ_FUZZ_INSERTED,");
        else lines.RemoveAt(entryLines[at]);

        return headerText[..open] + string.Join('\n', lines) + headerText[close..];
    }

    private static int ParseValue(string raw)
    {
        var shift = raw.IndexOf("<<", StringComparison.Ordinal);
        return shift < 0
            ? int.Parse(raw, CultureInfo.InvariantCulture)
            : 1 << int.Parse(raw[(shift + 2)..].Trim(), CultureInfo.InvariantCulture);
    }

    // Pascal to SCREAMING_SNAKE, breaking on a change of character class as
    // well as on a capital: the header writes OSC_52_READ, and breaking only on
    // capitals gives OSC52_READ, which then reports as "not in the typedef"
    // rather than as the naming mismatch it is.
    private static string ToScreamingSnake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && (char.IsUpper(pascal[i])
                          || (char.IsDigit(pascal[i]) && !char.IsDigit(pascal[i - 1]))))
            {
                sb.Append('_');
            }

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
        ParseHeaderEnum(ReadHeader(), typedefName, explicitValues: false);

    // Parses `typedef enum { ... } <typedefName>;` into name -> value, taken
    // from position or from an explicit initializer per `explicitValues`.
    private static Dictionary<string, int> ParseHeaderEnum(
        string headerText, string typedefName, bool explicitValues)
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

            var hasValue = m.Groups["value"].Success;

            // The two readings cannot be mixed. A positional read of an enum
            // that assigns even one value is wrong for everything after it, and
            // an explicit read of an enum that assigns none has nothing to read.
            // Whichever the caller asked for, every entry has to be that kind.
            Assert.True(
                hasValue == explicitValues,
                explicitValues
                    ? $"{typedefName} was read for explicit values but this entry has none: {line}"
                    : $"{typedefName} entry has an explicit value, ordinals are no " +
                      $"longer positional: {line}");

            entries[m.Groups["name"].Value] =
                explicitValues ? ParseValue(m.Groups["value"].Value) : index;
            index++;
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
