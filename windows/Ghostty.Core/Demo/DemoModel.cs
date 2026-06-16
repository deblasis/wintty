#if DEMO
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Core.Demo;

/// <summary>How the player advances between beats.</summary>
internal enum DemoMode
{
    Auto,
    Stepped,
}

/// <summary>
/// One demo beat. Heterogeneous fields keyed off <see cref="Type"/>; only the
/// fields relevant to a beat's type are populated. Flat (non-polymorphic) so
/// System.Text.Json source generation stays simple and AOT-safe. Mutable
/// get/set DTO to match the codebase's JSON model convention (SessionModel,
/// DiscoveryCache); init-only records lose absent-property initializers under
/// the source generator.
/// </summary>
internal sealed class DemoBeat
{
    /// <summary>caption | action | binding | type | key | config | command | keys | wait</summary>
    public string Type { get; set; } = "";

    // caption / type
    public string? Text { get; set; }

    // "action" beat: a PaneAction name (underscore/case tolerant), e.g. "split_vertical".
    // "config" beat: the config key to set, e.g. "theme" or "background-opacity".
    public string? Key { get; set; }

    // "config" beat: the value to write for Key, e.g. "catppuccin-mocha" or "0.85".
    public string? Value { get; set; }

    // "binding" beat: a libghostty binding action, e.g. "clear_screen"
    public string? Action { get; set; }

    // "key" beat: a named raw key, e.g. "enter", "up", "escape" (distinct from Key above)
    public string? Chord { get; set; }

    // type: send Return after the text so the shell runs it
    public bool Enter { get; set; }

    // caption display time; falls back to BeatGapMs
    public int? DurationMs { get; set; }

    // wait
    public int? Ms { get; set; }

    // type: per-character animation delay; falls back to script TypeDelayMs
    public int? TypeDelayMs { get; set; }
}

/// <summary>A full demo: ordered beats plus default timings.</summary>
internal sealed class DemoScript
{
    public string? Title { get; set; }
    public int TypeDelayMs { get; set; } = 45;
    public int BeatGapMs { get; set; } = 700;
    public List<DemoBeat> Beats { get; set; } = [];
}

// Source-generated metadata keeps deserialization off reflection so the Core
// trim/AOT analyzers stay quiet even though demo code never reaches the public
// AOT build.
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(DemoScript))]
internal sealed partial class DemoJsonContext : JsonSerializerContext
{
}
#endif
