using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

// Checks the managed payload structs against the C structs in
// include/ghostty.h, by computing the C layout rather than by restating it.
//
// GhosttyActionsLayoutTests asserts sizes and offsets against literals, and
// those literals were derived from the header by hand. That is not the
// tautology the ordinal pins were -- editing a managed struct does fail it --
// but it only ever looks at one side. A field appended to a C struct, or a
// type widened there, changes the real layout and fails nothing: the managed
// struct still has the size the literal names, the read at the old offset
// still succeeds, and it returns the wrong bytes.
//
// So this reads the field list out of the header, computes offsets with the
// x64 rules, and compares offset, type and name for each field plus the total
// size against Marshal.
//
// All four, because each alone has a blind spot. Offsets miss a widening that
// padding absorbs: uint16_t -> uint32_t leaves ResizeSplit at {0, 4} and eight
// bytes while the managed ushort reads the low half. Types miss a swap of two
// fields that share a type. Names catch that swap, at the cost of a rename map
// for the places C and C# genuinely disagree -- there is one, the `timetime_ms`
// typo that the managed side spells RuntimeMs.
//
// The type table refuses anything it does not know rather than guessing a
// size. A guess here would be a layout assertion resting on an invention. The
// one modelling gap that would fail GREEN rather than loudly is struct packing,
// so StructBody refuses a header carrying a pragma for it.
public class GhosttyStructHeaderParityTests
{
    private const string HeaderResource = "Ghostty.Tests.Interop.Header.ghostty.h";

    private static readonly Lazy<string> Header = new(LoadHeader);

    // `<type> <name>;` with an optional trailing comment. A pointer star may
    // sit on either side of the space.
    //
    // The star group is `(\*\s*(const\s*)?)*` rather than a single optional
    // star so pointer-to-pointer fields parse. The clipboard structs introduced
    // `const char *const *available`, which the single-star form could not
    // match at all: it left the name group trying to match `const`, the line
    // failed to parse, and the field silently vanished from the computed
    // layout. A field that disappears does not fail this test loudly -- it
    // shifts every later offset and changes the computed size, which is
    // exactly the kind of wrong-but-plausible answer this test exists to
    // prevent, so the parse has to handle the shape rather than skip it.
    private static readonly Regex FieldPattern = new(
        @"^\s*(?<type>[A-Za-z_][A-Za-z0-9_ ]*?(\s*\*\s*(const\s*)?)*)\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?://.*)?$",
        RegexOptions.Compiled);

    [Fact]
    public void Scrollbar_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionScrollbar>("ghostty_action_scrollbar_s");

    [Fact]
    public void MouseOverLink_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionMouseOverLink>("ghostty_action_mouse_over_link_s");

    [Fact]
    public void SizeLimit_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionSizeLimit>("ghostty_action_size_limit_s");

    [Fact]
    public void InitialSize_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionInitialSize>("ghostty_action_initial_size_s");

    [Fact]
    public void ResizeSplit_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionResizeSplit>("ghostty_action_resize_split_s");

    [Fact]
    public void MoveTab_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionMoveTab>("ghostty_action_move_tab_s");

    [Fact]
    public void ProgressReport_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionProgressReport>("ghostty_action_progress_report_s");

    // The clipboard trio. These are the highest-value pins in the file: unlike
    // the action structs, nothing else checks them. Both sides of the clipboard
    // boundary compile independently, so a drifted layout here produces a
    // callback that reads the wrong bytes rather than a build failure -- and
    // upstream ships no test for its own apprt clipboard layer either.
    [Fact]
    public void ClipboardContent_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyClipboardContent>("ghostty_clipboard_content_s");

    [Fact]
    public void ClipboardComplete_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyClipboardComplete>("ghostty_clipboard_complete_s");

    [Fact]
    public void ClipboardConfirm_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyClipboardConfirm>("ghostty_clipboard_confirm_s");

    // GhosttyClipboardLayout's constants, against the header directly.
    //
    // Production code reads and writes these structs by explicit offset
    // rather than through Marshal, so the constants -- not the managed
    // structs -- are what actually decides which bytes get read. Checking
    // them against the managed struct would only prove the two agree with
    // each other; checking them against the header is what makes them true.
    [Theory]
    [InlineData("ghostty_clipboard_content_s", "mime", GhosttyClipboardLayout.ContentMime)]
    [InlineData("ghostty_clipboard_content_s", "data", GhosttyClipboardLayout.ContentData)]
    [InlineData("ghostty_clipboard_content_s", "len", GhosttyClipboardLayout.ContentLen)]
    [InlineData("ghostty_clipboard_complete_s", "contents", GhosttyClipboardLayout.CompleteContents)]
    [InlineData("ghostty_clipboard_complete_s", "contents_len", GhosttyClipboardLayout.CompleteContentsLen)]
    [InlineData("ghostty_clipboard_complete_s", "available", GhosttyClipboardLayout.CompleteAvailable)]
    [InlineData("ghostty_clipboard_complete_s", "available_len", GhosttyClipboardLayout.CompleteAvailableLen)]
    [InlineData("ghostty_clipboard_complete_s", "confirmed", GhosttyClipboardLayout.CompleteConfirmed)]
    [InlineData("ghostty_clipboard_complete_s", "remember", GhosttyClipboardLayout.CompleteRemember)]
    [InlineData("ghostty_clipboard_confirm_s", "contents", GhosttyClipboardLayout.ConfirmContents)]
    [InlineData("ghostty_clipboard_confirm_s", "contents_len", GhosttyClipboardLayout.ConfirmContentsLen)]
    [InlineData("ghostty_clipboard_confirm_s", "available", GhosttyClipboardLayout.ConfirmAvailable)]
    [InlineData("ghostty_clipboard_confirm_s", "available_len", GhosttyClipboardLayout.ConfirmAvailableLen)]
    [InlineData("ghostty_clipboard_confirm_s", "name", GhosttyClipboardLayout.ConfirmName)]
    [InlineData("ghostty_clipboard_confirm_s", "can_remember", GhosttyClipboardLayout.ConfirmCanRemember)]
    public void ClipboardLayout_Offsets_Match_Header(string typedefName, string field, int expected)
    {
        var layout = CLayoutOf(typedefName);
        var match = layout.Fields.SingleOrDefault(f => f.Name == field);
        Assert.True(match is not null, $"{typedefName} has no field named \"{field}\" in the header");
        Assert.Equal(expected, match!.Offset);
    }

    [Theory]
    [InlineData("ghostty_clipboard_content_s", GhosttyClipboardLayout.ContentSize)]
    [InlineData("ghostty_clipboard_complete_s", GhosttyClipboardLayout.CompleteSize)]
    [InlineData("ghostty_clipboard_confirm_s", GhosttyClipboardLayout.ConfirmSize)]
    public void ClipboardLayout_Sizes_Match_Header(string typedefName, int expected) =>
        Assert.Equal(expected, CLayoutOf(typedefName).Size);

    // The one rename in the set: the header's field carries a `timetime_ms`
    // typo that the managed side spells RuntimeMs. Declared rather than
    // tolerated, so the name check stays on for every other field.
    [Fact]
    public void ChildExited_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyChildExited>(
            "ghostty_surface_message_childexited_s",
            new Dictionary<string, string> { ["timetime_ms"] = "RuntimeMs" });

    [Fact]
    public void StartSearch_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionStartSearch>("ghostty_action_start_search_s");

    [Fact]
    public void SearchTotal_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionSearchTotal>("ghostty_action_search_total_s");

    [Fact]
    public void SearchSelected_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionSearchSelected>("ghostty_action_search_selected_s");

    [Fact]
    public void DesktopNotification_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionDesktopNotification>(
            "ghostty_action_desktop_notification_s");

    /// <param name="renamed">
    /// C field name to managed field name, for the places the two genuinely
    /// disagree. Explicit rather than inferred, because "the names need not
    /// match" is what lets a reorder through: with names compared, swapping two
    /// fields of the same type is caught, and that is a swap neither the
    /// offsets nor the types can see.
    /// </param>
    private static void AssertLayoutMatchesHeader<T>(
        string typedefName,
        IReadOnlyDictionary<string, string>? renamed = null) where T : struct
    {
        // x64 is what the whole table below assumes. On a 32-bit runtime every
        // pointer-sized field would compute to 8 while Marshal reports 4, and
        // eleven tests would report an ABI break that is really this test not
        // modelling the platform.
        Assert.True(IntPtr.Size == 8, "these layouts are computed for a 64-bit runtime");

        var expected = CLayoutOf(typedefName);

        // Ordered by where the runtime actually put them, which is the order
        // that has to match C. Declaration order from reflection would be the
        // same today and is not guaranteed to be.
        var managed = typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => (f.Name, f.FieldType, Offset: (int)Marshal.OffsetOf<T>(f.Name)))
            .OrderBy(f => f.Offset)
            .ToList();

        Assert.True(
            expected.Fields.Count == managed.Count,
            $"{typeof(T).Name} has {managed.Count} fields but {typedefName} has " +
            $"{expected.Fields.Count} ({string.Join(", ", expected.Fields.Select(f => f.Name))}); " +
            "a field added on the C side moves everything after it");

        var problems = new List<string>();
        for (var i = 0; i < managed.Count; i++)
        {
            var (name, type, offset) = managed[i];
            var c = expected.Fields[i];

            if (offset != c.Offset)
            {
                problems.Add($"  {name} is at +{offset} but {c.Name} ({c.Type}) is at +{c.Offset}");
            }

            // Offsets alone let a widening that padding absorbs through:
            // uint16_t amount -> uint32_t keeps ResizeSplit at {0, 4} and 8
            // bytes while the managed ushort silently reads the low half.
            if (!TypeMatches(type, c.Type))
            {
                problems.Add($"  {name} is {type.Name} but {c.Name} is {c.Type}");
            }

            var expectedName = renamed is not null && renamed.TryGetValue(c.Name, out var mapped)
                ? mapped
                : ToPascal(c.Name);
            if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            {
                problems.Add(
                    $"  field {i} is {name} but {typedefName} calls it {c.Name} " +
                    $"(expected {expectedName}); if the rename is deliberate, pass it in `renamed`");
            }
        }

        if (Marshal.SizeOf<T>() != expected.Size)
        {
            problems.Add(
                $"  sizeof is {Marshal.SizeOf<T>()} but {typedefName} computes to {expected.Size}");
        }

        Assert.True(
            problems.Count == 0,
            $"{typeof(T).Name} does not match {typedefName} in include/ghostty.h:\n" +
            string.Join("\n", problems));
    }

    // Which managed types a C type may be spelled as. An enum stands in for the
    // C enum it mirrors, so any 4-byte-backed enum is accepted; which enum it is
    // is GhosttyActionTagHeaderParityTests's business, not this file's.
    private static bool TypeMatches(Type managed, string cType)
    {
        if (managed.IsEnum)
        {
            var backing = Enum.GetUnderlyingType(managed);
            return cType.StartsWith("ghostty_", StringComparison.Ordinal)
                   && cType.EndsWith("_e", StringComparison.Ordinal)
                   && (backing == typeof(int) || backing == typeof(uint));
        }

        if (cType.EndsWith("*", StringComparison.Ordinal))
        {
            return managed == typeof(IntPtr) || managed == typeof(UIntPtr);
        }

        return cType switch
        {
            "int8_t" => managed == typeof(sbyte),
            "uint8_t" or "bool" or "char" => managed == typeof(byte),
            "int16_t" => managed == typeof(short),
            "uint16_t" => managed == typeof(ushort),
            "int32_t" or "int" => managed == typeof(int),
            "uint32_t" => managed == typeof(uint),
            "int64_t" => managed == typeof(long),
            "uint64_t" => managed == typeof(ulong),
            "float" => managed == typeof(float),
            "double" => managed == typeof(double),
            "ssize_t" or "intptr_t" => managed == typeof(IntPtr),
            "size_t" or "uintptr_t" => managed == typeof(UIntPtr) || managed == typeof(IntPtr),
            _ => false,
        };
    }

    private static string ToPascal(string snake) =>
        string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private sealed record CField(string Name, string Type, int Offset);

    private sealed record CLayout(IReadOnlyList<CField> Fields, int Size);

    // Standard x64 layout: each field is placed at the next offset that is a
    // multiple of its alignment, and the total is rounded up to the largest
    // alignment in the struct. Every type here has alignment equal to its size,
    // so one table serves for both.
    private static CLayout CLayoutOf(string typedefName)
    {
        var body = StructBody(typedefName);

        var fields = new List<CField>();
        var offset = 0;
        var maxAlign = 1;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var m = FieldPattern.Match(line);
            Assert.True(m.Success, $"unparsed field in {typedefName}: {line}");

            var type = Regex.Replace(m.Groups["type"].Value.Trim(), @"\s+", " ");
            var size = SizeOfCType(type, typedefName, line);

            offset = Align(offset, size);
            fields.Add(new CField(m.Groups["name"].Value, type, offset));
            offset += size;
            maxAlign = Math.Max(maxAlign, size);
        }

        // A struct that parsed to nothing would satisfy every comparison below
        // against a managed struct that also had no fields, and there is no
        // such struct here.
        Assert.True(
            fields.Count > 0,
            $"parsed no fields out of {typedefName}; the scan is broken, not the struct");

        return new CLayout(fields, Align(offset, maxAlign));
    }

    private static int Align(int offset, int alignment) =>
        (offset + alignment - 1) / alignment * alignment;

    // Refuses what it does not know. Inventing a size for an unrecognised type
    // would make every offset after it a fiction while still reading green.
    private static int SizeOfCType(string type, string typedefName, string line)
    {
        if (type.EndsWith("*", StringComparison.Ordinal)) return 8;

        // A C enum is an int in every ABI this ships on, and the header's enums
        // all fit in one.
        if (type.StartsWith("ghostty_", StringComparison.Ordinal)
            && type.EndsWith("_e", StringComparison.Ordinal))
        {
            return 4;
        }

        switch (type)
        {
            case "int8_t":
            case "uint8_t":
            case "bool":
            case "char":
                return 1;
            case "int16_t":
            case "uint16_t":
                return 2;
            case "int32_t":
            case "uint32_t":
            case "int":
            case "float":
                return 4;
            case "int64_t":
            case "uint64_t":
            case "double":
            case "size_t":
            case "ssize_t":
            case "uintptr_t":
            case "intptr_t":
                return 8;
            default:
                Assert.Fail(
                    $"{typedefName}: no size known for C type \"{type}\" in \"{line}\". " +
                    "Add it to SizeOfCType rather than letting the layout be guessed.");
                return 0;
        }
    }

    // Located from the closing line backwards, like the enum reader: the
    // opening `typedef struct {` is far from unique in this header.
    private static string StructBody(string typedefName)
    {
        var header = Header.Value;

        // Everything else here fails loudly when it cannot model something.
        // Packing is the exception: under a pragma the computed C layout stays
        // natural, the managed sequential layout stays natural, the two agree,
        // and the real ABI is neither.
        Assert.DoesNotContain("#pragma pack", header, StringComparison.Ordinal);

        var close = header.IndexOf("} " + typedefName + ";", StringComparison.Ordinal);
        Assert.True(close >= 0, $"{typedefName} not found in include/ghostty.h");

        var open = header.LastIndexOf("typedef struct {", close, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no struct body precedes {typedefName} in include/ghostty.h");

        var body = header[(open + "typedef struct {".Length)..close];

        // The backwards search only matches the anonymous `typedef struct {`
        // form. Rewritten as `typedef struct name { ... } name_s;` it would
        // walk past and return the previous struct's body plus a stray closing
        // line; that line fails the field pattern, but the message would name
        // the wrong struct.
        Assert.DoesNotContain("}", body, StringComparison.Ordinal);

        return body;
    }

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
