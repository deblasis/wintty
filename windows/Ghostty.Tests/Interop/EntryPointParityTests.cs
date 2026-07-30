using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins that every entry point the managed code imports is actually exported by
// libghostty.
//
// A [LibraryImport] naming a symbol libghostty never exports is invisible to
// `dotnet build` and to JIT dev runs, because neither links native symbols. It
// only surfaces as an unresolved symbol during the NativeAOT publish
// (DirectPInvoke + static ghostty-static.lib), i.e. at release time. The
// inspector's DirectX12 swap chain entry points sat broken this way and blocked
// every AOT release.
//
// This scans the zig `export fn` sites, NOT include/ghostty.h: exports added by
// this fork are routinely absent from the header (ghostty_cli_*,
// ghostty_surface_list_themes_*, ghostty_inspector_zoom_*), so the header would
// report symbols that link perfectly well. Both sets of files are embedded by
// Ghostty.Tests.csproj.
//
// Known blind spot: the scan cannot see comptime platform gating. Darwin-only
// exports (ghostty_inspector_metal_*, inside `const Darwin = struct`) look
// exported on every platform, so importing one from Windows code would pass
// here and still fail the Windows publish.
public class EntryPointParityTests
{
    private const string ImportResourcePrefix = "Ghostty.Tests.Interop.Imports.";
    private const string ExportResourcePrefix = "Ghostty.Tests.Interop.Exports.";

    // EntryPoint = "ghostty_x" on a [LibraryImport] attribute. Anchored on the
    // prefix so a Win32 import added to the same file does not start demanding
    // a zig export.
    private static readonly Regex EntryPointPattern = new(
        @"EntryPoint\s*=\s*""(?<name>ghostty_[A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    // `export fn name(`, allowing the `pub` and whitespace variants zig permits.
    private static readonly Regex ExportPattern = new(
        @"export\s+fn\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    // The other export form: `@export(&fn, .{ .name = "ghostty_x" })`, used for
    // conditionally exported symbols (ghostty_init_wide is Windows-only).
    private static readonly Regex ExportNameFieldPattern = new(
        @"\.name\s*=\s*""(?<name>ghostty_[A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryImportedEntryPointIsExportedByLibghostty()
    {
        var imported = ReadSites(ImportResourcePrefix)
            .SelectMany(src => Extract(src, EntryPointPattern))
            .ToHashSet(StringComparer.Ordinal);

        var exported = ReadSites(ExportResourcePrefix)
            .SelectMany(src => Extract(src, ExportPattern)
                .Concat(Extract(src, ExportNameFieldPattern)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(imported);
        Assert.NotEmpty(exported);

        var missing = imported
            .Where(name => !exported.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0)
        {
            Assert.Fail(
                "Imported entry points that libghostty does not export. These link " +
                "in Debug but fail the NativeAOT publish. If the symbol does exist, " +
                "its zig file is probably missing from the Exports list in " +
                "Ghostty.Tests.csproj:\n" +
                string.Join("\n", missing.Select(n => $"  {n}")));
        }
    }

    // Guards the csproj resource lists. Without this, losing an embedded file
    // would make the parity test scan an empty set and pass vacuously.
    [Fact]
    public void ImportAndExportSitesAreEmbedded()
    {
        var names = Assembly.GetExecutingAssembly().GetManifestResourceNames();

        var expected = new[]
        {
            ImportResourcePrefix + "NativeMethods.cs",
            ImportResourcePrefix + "LibGhosttyBuildInfo.cs",
            ExportResourcePrefix + "apprt.embedded.zig",
            ExportResourcePrefix + "benchmark.CApi.zig",
            ExportResourcePrefix + "config.CApi.zig",
            ExportResourcePrefix + "log_bridge.zig",
            ExportResourcePrefix + "main_c.zig",
        };

        var absent = expected.Where(e => !names.Contains(e, StringComparer.Ordinal)).ToList();

        if (absent.Count > 0)
        {
            Assert.Fail(
                "Embedded resources missing from Ghostty.Tests.csproj:\n" +
                string.Join("\n", absent.Select(n => $"  {n}")));
        }
    }

    [Fact]
    public void EntryPointExtractionReadsAttributeLiterals()
    {
        const string source =
            "[LibraryImport(Dll, EntryPoint = \"ghostty_app_new\")]\n" +
            "internal static partial IntPtr AppNew();\n" +
            "[LibraryImport(\"user32\", EntryPoint = \"MessageBoxW\")]\n" +
            "private static partial int MessageBox();\n";

        var names = Extract(source, EntryPointPattern);

        Assert.Contains("ghostty_app_new", names);
        Assert.DoesNotContain("MessageBoxW", names);
    }

    [Fact]
    public void ExportExtractionReadsZigDeclarations()
    {
        const string source =
            "export fn ghostty_app_new(opts: *Options) ?*App {\n" +
            "export fn ghostty_inspector_directx12_surface_present(_: *Inspector) void {}\n" +
            "fn not_exported(x: u32) void {}\n";

        var names = Extract(source, ExportPattern);

        Assert.Contains("ghostty_app_new", names);
        Assert.Contains("ghostty_inspector_directx12_surface_present", names);
        Assert.DoesNotContain("not_exported", names);
    }

    [Fact]
    public void ExportExtractionReadsConditionalExportBlocks()
    {
        const string source =
            "comptime {\n" +
            "    if (builtin.os.tag == .windows) @export(&ghosttyInitWide, .{\n" +
            "        .name = \"ghostty_init_wide\",\n" +
            "        .linkage = .strong,\n" +
            "    });\n" +
            "}\n" +
            "const cfg = .{ .name = \"not_an_export\" };\n";

        var names = Extract(source, ExportNameFieldPattern);

        Assert.Contains("ghostty_init_wide", names);
        Assert.DoesNotContain("not_an_export", names);
    }

    // Commented-out code must not count. A stale `// export fn ghostty_x(`
    // would otherwise satisfy an import that no longer resolves.
    [Fact]
    public void ScannerIgnoresLineComments()
    {
        const string zig =
            "// export fn ghostty_removed(ptr: *App) void {}\n" +
            "export fn ghostty_live(ptr: *App) void {}\n";
        const string cs =
            "// [LibraryImport(Dll, EntryPoint = \"ghostty_removed\")]\n" +
            "[LibraryImport(Dll, EntryPoint = \"ghostty_live\")]\n";

        var exports = Extract(zig, ExportPattern);
        var imports = Extract(cs, EntryPointPattern);

        Assert.Equal(new[] { "ghostty_live" }, exports);
        Assert.Equal(new[] { "ghostty_live" }, imports);
    }

    private static SortedSet<string> Extract(string source, Regex pattern)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in source.Split('\n'))
        {
            foreach (Match match in pattern.Matches(StripLineComment(line)))
            {
                names.Add(match.Groups["name"].Value);
            }
        }
        return names;
    }

    // Both corpora are zig and C#, which share `//` line-comment syntax. String
    // literals containing "//" would be truncated, but no entry point or export
    // declaration contains one.
    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static IEnumerable<string> ReadSites(string prefix)
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(ReadResource)
            .ToList();
    }

    private static string ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
