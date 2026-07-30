using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins that every entry point NativeMethods.cs imports is actually exported by
// libghostty.
//
// A [LibraryImport] naming a symbol libghostty never exports is invisible to
// `dotnet build` and to JIT dev runs, because neither links native symbols. It
// only surfaces as an unresolved-symbol failure during the NativeAOT publish
// (DirectPInvoke + static ghostty-static.lib), i.e. at release time. The
// inspector's DirectX12 swap chain entry points sat broken this way and blocked
// every AOT release.
//
// This scans the zig `export fn` declarations, NOT include/ghostty.h: exports
// added by this fork are routinely absent from the header (ghostty_cli_*,
// ghostty_surface_list_themes_*, ghostty_inspector_zoom_* all export fine while
// undeclared), so the header would produce false failures. The export sites are
// embedded by Ghostty.Tests.csproj.
public class EntryPointParityTests
{
    private const string NativeMethodsResource = "Ghostty.Tests.Interop.NativeMethods.cs";
    private const string ExportResourcePrefix = "Ghostty.Tests.Interop.Exports.";

    // EntryPoint = "name" on the [LibraryImport] attribute.
    private static readonly Regex EntryPointPattern = new(
        @"EntryPoint\s*=\s*""(?<name>[A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    // `export fn name(`, allowing the `pub` and whitespace variants zig permits.
    private static readonly Regex ExportPattern = new(
        @"export\s+fn\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    // The other export form: `@export(&fn, .{ .name = "ghostty_x" })`, used for
    // symbols that are conditionally exported (e.g. ghostty_init_wide, which is
    // Windows-only). Anchored on the ghostty_ prefix so it cannot match an
    // unrelated `.name =` field.
    private static readonly Regex ExportNameFieldPattern = new(
        @"\.name\s*=\s*""(?<name>ghostty_[A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryImportedEntryPointIsExportedByLibghostty()
    {
        var imported = ExtractMatches(ReadNativeMethods(), EntryPointPattern);
        var exported = ReadExportSites()
            .SelectMany(src => ExtractMatches(src, ExportPattern)
                .Concat(ExtractMatches(src, ExportNameFieldPattern)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(imported);
        Assert.NotEmpty(exported);

        var missing = imported
            .Where(name => !exported.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "NativeMethods.cs imports entry points that libghostty does not export. " +
            "These link in Debug but fail the NativeAOT publish:\n" +
            string.Join("\n", missing.Select(n => $"  {n}")));
    }

    // Guards the csproj resource glob: if the embedded export sites go missing
    // or get renamed, the parity test above would silently pass with an empty
    // export set instead of failing loudly.
    [Fact]
    public void ExportSitesAreEmbedded()
    {
        var sites = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ExportResourcePrefix, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            sites.Count >= 5,
            $"Expected the libghostty export sites to be embedded, found {sites.Count}: " +
            string.Join(", ", sites));
    }

    [Fact]
    public void EntryPointExtractionReadsAttributeLiterals()
    {
        const string source =
            "[LibraryImport(Dll, EntryPoint = \"ghostty_app_new\")]\n" +
            "internal static partial IntPtr AppNew();\n" +
            "[LibraryImport(Dll, EntryPoint = \"ghostty_app_free\")]\n" +
            "internal static partial void AppFree(IntPtr app);\n";

        Assert.Equal(
            new[] { "ghostty_app_free", "ghostty_app_new" },
            ExtractMatches(source, EntryPointPattern).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ExportExtractionReadsZigDeclarations()
    {
        const string source =
            "export fn ghostty_app_new(opts: *Options) ?*App {\n" +
            "export fn ghostty_inspector_directx12_surface_present(_: *Inspector) void {}\n" +
            "fn not_exported(x: u32) void {}\n";

        var names = ExtractMatches(source, ExportPattern);

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

        var names = ExtractMatches(source, ExportNameFieldPattern);

        Assert.Contains("ghostty_init_wide", names);
        Assert.DoesNotContain("not_an_export", names);
    }

    private static SortedSet<string> ExtractMatches(string source, Regex pattern)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in pattern.Matches(source))
        {
            names.Add(match.Groups["name"].Value);
        }
        return names;
    }

    private static string ReadNativeMethods() => ReadResource(NativeMethodsResource);

    private static IEnumerable<string> ReadExportSites()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ExportResourcePrefix, StringComparison.Ordinal))
            .Select(ReadResource)
            .ToList();
    }

    private static string ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
