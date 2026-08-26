using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Diagnostics;

namespace Ghostty.Commands;

/// <summary>
/// The deliberate crash triggers, as palette entries.
///
/// Same set and same implementation as <c>wintty +crash &lt;kind&gt;</c>:
/// the rows are projected from <see cref="CrashKinds.All"/> and each one
/// calls straight back into <c>CrashTrigger.Run</c>. The palette is the
/// only way to reach the surface-bound kinds at all, because a binding
/// action has no surface to land on before a window exists.
///
/// Present in every build, including shipped installers. Capture has to be
/// verifiable in the builds users actually run, and a trigger that is
/// compiled out of Release makes the one configuration that matters the one
/// configuration nobody can test. The entries are prefixed "Debug:" and sort
/// last, and they match only on their title, so reaching one takes a
/// deliberate search rather than a word that happens to appear in a
/// description. There is no runtime gate and none is offered: a flag a user
/// could flip is a flag that ends up flipped, and a gate would put the
/// shipped build back out of reach of the harness.
/// </summary>
internal sealed class CrashCommandSource : ICommandSource
{
    // Segoe Fluent "Warning" (E7BA). Built from the code point rather than
    // embedded, matching DemoCommandSource: private-use-area characters in
    // source survive an editor round trip only by luck.
    private static readonly string WarningGlyph = ((char)0xE7BA).ToString();

    private readonly IReadOnlyList<CommandItem> _commands;

    /// <param name="run">
    /// Runs one kind by its <see cref="CrashKind.Id"/>. Takes the id rather
    /// than a prepared delegate so the mechanism stays in one place, and so
    /// this source cannot grow a second opinion about what a kind does.
    /// </param>
    public CrashCommandSource(Action<string> run)
    {
        _commands = CrashKinds.All
            .Select(kind => new CommandItem
            {
                // Namespaced so a kind id can never collide with a binding
                // action id in the frecency store, which keys off this.
                Id = $"crash:{kind.Id}",
                Title = kind.Title,
                Description = kind.Description,
                Category = CommandCategory.Debug,
                LeadingIcon = WarningGlyph,
                Execute = _ => run(kind.Id),
            })
            .ToList();
    }

    public IReadOnlyList<CommandItem> GetCommands() => _commands;

    public void Refresh() { /* static entries */ }
}
