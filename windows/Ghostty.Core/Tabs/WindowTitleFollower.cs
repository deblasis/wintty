using System;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Keeps the window caption on the selected tab's label. The caption is
/// words only (<see cref="TabModel.WordTitle"/>), so the home glyph the
/// strips draw reads "Home" here.
///
/// Set once at construction, because nothing has raised
/// <see cref="TabManager.WindowTitleChanged"/> yet when the window is built,
/// and then on every raise: TabManager raises it on a tab switch and when
/// the selected tab's label moves, and never for a background tab.
///
/// <see cref="Sync"/> is the only place the caption is computed, so a
/// manager that can be empty has exactly one line to guard.
/// </summary>
internal sealed class WindowTitleFollower
{
    private readonly TabManager _tabs;
    private readonly Action<string> _setTitle;

    public WindowTitleFollower(TabManager tabs, Action<string> setTitle)
    {
        _tabs = tabs;
        _setTitle = setTitle;
        _tabs.WindowTitleChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>Write the selected tab's label to the caption.</summary>
    public void Sync() => _setTitle(_tabs.ActiveTab.WordTitle);
}
