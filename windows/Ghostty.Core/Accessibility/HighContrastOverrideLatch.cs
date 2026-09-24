namespace Ghostty.Core.Accessibility;

/// <summary>
/// The High Contrast palette the config service is asked to layer, kept
/// apart from the one the running config was actually built with.
/// </summary>
/// <remarks>
/// <para>The override reaches the terminal only through a reload, and a
/// reload can decline. One field holding both meanings forced a choice on
/// every decline. Leaving it moved made the "already on this palette" guard
/// swallow every later request, so one declined reload turned High Contrast
/// off for the life of the process. Putting it back lost the request
/// instead: a reload declined while a deleted config file was still being
/// confirmed dropped the toggle that caused it, and the user had to toggle
/// twice (wintty#1155).</para>
///
/// <para>Two values need no choice. <see cref="Wanted"/> is what the next
/// reload builds with, so a declined request rides the next reload that
/// applies, whoever causes it. <see cref="Applied"/> is what the running
/// config holds, and a request is skipped only when both already match
/// it.</para>
///
/// <para>A third value keeps that from looping. <see cref="Attempted"/> is
/// what the last APPLIED reload tried to layer, whether or not its override
/// file could be written. When the write fails (a full disk, a read-only
/// file, a state directory the user cannot write), the reload still applies
/// without the layer, raises ConfigChanged, and HighContrastMonitor asks for
/// the same palette again. Skipping only on Applied let that request through
/// every time, and the app reloaded forever. A request for the palette the
/// last applied reload already tried is skipped too: it is retried by the
/// next reload that happens for any other reason, which builds with
/// <see cref="Wanted"/> and so writes the file again, or by a real change of
/// palette.</para>
///
/// <para>Not thread safe: the monitor marshals to the UI thread, and every
/// reload runs there.</para>
/// </remarks>
public sealed class HighContrastOverrideLatch
{
    /// <summary>What the next reload builds with.</summary>
    public HighContrastColors? Wanted { get; private set; }

    /// <summary>What the config in force was built with.</summary>
    public HighContrastColors? Applied { get; private set; }

    /// <summary>
    /// Record a request, and answer whether a reload is needed to reach it.
    /// </summary>
    public bool Request(HighContrastColors? colors)
    {
        if (Wanted == colors && (Applied == colors || Attempted == colors)) return false;
        Wanted = colors;
        return true;
    }

    /// <summary>
    /// What the last applied reload tried to layer: the value of
    /// <see cref="Wanted"/> it was built with, layered or not.
    /// </summary>
    public HighContrastColors? Attempted { get; private set; }

    /// <summary>
    /// A reload applied a config that tried to layer
    /// <paramref name="attempted"/> (the value of <see cref="Wanted"/> read
    /// when it was built) and actually carries <paramref name="built"/>:
    /// the same palette, or null when its override file could not be
    /// written.
    /// </summary>
    public void MarkApplied(HighContrastColors? built, HighContrastColors? attempted)
    {
        Applied = built;
        Attempted = attempted;
    }
}
