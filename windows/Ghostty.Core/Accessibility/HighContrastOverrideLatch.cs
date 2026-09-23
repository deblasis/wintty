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
        if (Wanted == colors && Applied == colors) return false;
        Wanted = colors;
        return true;
    }

    /// <summary>
    /// A reload applied a config built with <paramref name="built"/>, the
    /// value of <see cref="Wanted"/> read when it was built.
    /// </summary>
    public void MarkApplied(HighContrastColors? built) => Applied = built;
}
