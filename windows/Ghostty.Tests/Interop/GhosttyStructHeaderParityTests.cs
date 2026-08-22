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
// x64 rules, and compares against Marshal. Fields are matched by POSITION, not
// by name: C is positional, and the names do not always agree anyway --
// ghostty_surface_message_childexited_s carries the `timetime_ms` typo that
// the managed side spells RuntimeMs.
//
// The type table refuses anything it does not know rather than guessing a
// size. A guess here would be a layout assertion resting on an invention.
public class GhosttyStructHeaderParityTests
{
    private const string HeaderResource = "Ghostty.Tests.Interop.Header.ghostty.h";

    private static readonly Lazy<string> Header = new(LoadHeader);

    // `<type> <name>;` with an optional trailing comment. A pointer star may
    // sit on either side of the space.
    private static readonly Regex FieldPattern = new(
        @"^\s*(?<type>[A-Za-z_][A-Za-z0-9_ ]*?\s*\*?)\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?://.*)?$",
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

    [Fact]
    public void ChildExited_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyChildExited>("ghostty_surface_message_childexited_s");

    [Fact]
    public void StartSearch_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionStartSearch>("ghostty_action_start_search_s");

    [Fact]
    public void SearchTotal_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionSearchTotal>("ghostty_action_search_total_s");

    [Fact]
    public void SearchSelected_Layout_Matches_Header() =>
        AssertLayoutMatchesHeader<GhosttyActionSearchSelected>("ghostty_action_search_selected_s");

    private static void AssertLayoutMatchesHeader<T>(string typedefName) where T : struct
    {
        var expected = CLayoutOf(typedefName);

        // Ordered by where the runtime actually put them, which is the order
        // that has to match C. Declaration order from reflection would be the
        // same today and is not guaranteed to be.
        var managed = typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => (f.Name, Offset: (int)Marshal.OffsetOf<T>(f.Name)))
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
            if (managed[i].Offset != expected.Fields[i].Offset)
            {
                problems.Add(
                    $"  {managed[i].Name} is at +{managed[i].Offset} but " +
                    $"{expected.Fields[i].Name} ({expected.Fields[i].Type}) is at " +
                    $"+{expected.Fields[i].Offset}");
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

        var close = header.IndexOf("} " + typedefName + ";", StringComparison.Ordinal);
        Assert.True(close >= 0, $"{typedefName} not found in include/ghostty.h");

        var open = header.LastIndexOf("typedef struct {", close, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no struct body precedes {typedefName} in include/ghostty.h");

        return header[(open + "typedef struct {".Length)..close];
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
