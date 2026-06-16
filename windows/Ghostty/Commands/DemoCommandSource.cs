#if DEMO
using System;
using System.Collections.Generic;
using Ghostty.Core.Demo;

namespace Ghostty.Commands;

/// <summary>
/// Surfaces the two demo entries in the command palette. Only constructed when
/// the WINTTY_DEMO env var is present (gated at the call site in MainWindow),
/// so in a demo build with the var unset the palette is unchanged.
/// </summary>
internal sealed class DemoCommandSource : ICommandSource
{
    // Segoe Fluent glyphs: Play (E768), FastForward (EB9D). Built from code
    // points to avoid embedding private-use-area characters in source.
    private static readonly string PlayGlyph = ((char)0xE768).ToString();
    private static readonly string StepGlyph = ((char)0xEB9D).ToString();

    private readonly IReadOnlyList<CommandItem> _commands;

    public DemoCommandSource(Action<DemoMode> start)
    {
        _commands = new[]
        {
            new CommandItem
            {
                Id = "demo:auto",
                Title = "Start Demo (Auto)",
                Description = "Play the demo script hands-off for recording",
                Category = CommandCategory.Demo,
                LeadingIcon = PlayGlyph,
                Execute = _ => start(DemoMode.Auto),
            },
            new CommandItem
            {
                Id = "demo:stepped",
                Title = "Start Demo (Stepped)",
                Description = "Play the demo script, advancing with Space / Right arrow",
                Category = CommandCategory.Demo,
                LeadingIcon = StepGlyph,
                Execute = _ => start(DemoMode.Stepped),
            },
        };
    }

    public IReadOnlyList<CommandItem> GetCommands() => _commands;

    public void Refresh() { /* static entries */ }
}
#endif
