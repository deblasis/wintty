using System;
using System.ComponentModel;
using System.Globalization;

namespace Ghostty.Core.Search;

/// <summary>
/// Observable state for in-pane scrollback search. Pure logic, no
/// WinUI references: the SearchBarControl owns an instance of this
/// type and binds its view to the properties exposed here, while the
/// per-pane controller mutates them in response to libghostty match
/// callbacks.
///
/// <see cref="IsOpen"/> tracks whether the search UI is visible.
/// <see cref="Needle"/> is the current query text. <see cref="Total"/>
/// is the match count reported by libghostty and <see cref="Selected"/>
/// is the zero-based index of the highlighted match (or -1 when no
/// match is selected). <see cref="CounterText"/> is a localizable-ready
/// formatted string suitable for direct display next to the search box.
///
/// <see cref="Reset"/> restores defaults but does not touch
/// <see cref="IsOpen"/>'s coupling to UI lifetime: the controller is
/// expected to close the UI separately so it can choose when to do so
/// (e.g. on Escape vs. on focus loss).
/// </summary>
public sealed class SearchState : INotifyPropertyChanged
{
    private bool _isOpen;
    private string _needle = string.Empty;
    // Starts inactive, not zero: zero means "searched and found nothing", so
    // a fresh bar would claim "No matches" for the debounce plus round-trip
    // before its first search has run.
    private long _total = NoSearch;
    private long _selected = -1;

    /// <summary>
    /// The <see cref="Total"/> value meaning "no search is running behind
    /// the bar", as distinct from a search that ran and matched nothing.
    /// </summary>
    public const long NoSearch = -1;

    /// <summary>True when the search bar is visible.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;
            _isOpen = value;
            Notify(nameof(IsOpen));
        }
    }

    /// <summary>Current search query. Empty cancels the active search.</summary>
    /// <remarks>
    /// Must stay a verbatim passthrough (only null-coalescing): the
    /// SearchBar needle TextBox binds here TwoWay with
    /// UpdateSourceTrigger=PropertyChanged, so any transform (trim,
    /// case-fold, normalize) would write a different string back into the
    /// focused box mid-edit and corrupt the caret. The Ordinal dedupe below
    /// is also what keeps the libghostty needle echo from looping.
    /// </remarks>
    public string Needle
    {
        get => _needle;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_needle, next, StringComparison.Ordinal)) return;
            _needle = next;
            Notify(nameof(Needle));
            Notify(nameof(CounterText));
        }
    }

    /// <summary>
    /// Total match count reported by libghostty, or negative when no search
    /// is active behind the bar.
    /// </summary>
    /// <remarks>
    /// libghostty encodes "no total" as -1 (apprt SearchTotal.cval maps a
    /// null total onto it), and it sends exactly that when the search thread
    /// quits. Negative therefore has to mean "nothing to count", not a count
    /// of -1, or the bar renders "0 of -1" from the moment a search is torn
    /// down.
    /// </remarks>
    public long Total
    {
        get => _total;
        set
        {
            if (_total == value) return;
            _total = value;
            Notify(nameof(Total));
            Notify(nameof(CounterText));
        }
    }

    /// <summary>
    /// Zero-based index of the currently selected match, or -1 when no
    /// match is selected (e.g. before the first navigation step, or
    /// when the needle has no matches).
    /// </summary>
    public long Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Notify(nameof(Selected));
            Notify(nameof(CounterText));
        }
    }

    /// <summary>
    /// Formatted match counter for direct display: empty when the needle is
    /// empty or no search is active, "No matches" when the needle has no
    /// hits, "{Selected+1} of {Total}" when a match is selected, or
    /// "0 of {Total}" when matches exist but none is selected yet.
    /// </summary>
    public string CounterText
    {
        get
        {
            if (_needle.Length == 0) return string.Empty;
            if (_total < 0) return string.Empty;   // no search running

            if (_total == 0) return "No matches";
            if (_selected >= 0)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} of {1}",
                    _selected + 1,
                    _total);
            }
            return string.Format(
                CultureInfo.CurrentCulture,
                "0 of {0}",
                _total);
        }
    }

    /// <summary>
    /// Restores all fields to their default values: closes the bar,
    /// clears the needle, marks the search inactive, and unselects. The
    /// reverse coupling is intentionally absent -- setting
    /// <see cref="IsOpen"/> to <c>false</c> on its own does NOT reset
    /// the search, so the controller can pick its own timing (e.g.
    /// hide the bar on focus loss but preserve the query for restore).
    /// </summary>
    public void Reset()
    {
        IsOpen = false;
        Needle = string.Empty;
        MarkInactive();
    }

    /// <summary>
    /// Record that the search behind the bar has been torn down, without
    /// touching the needle: the query is kept so it can be re-run when the
    /// bar reopens. Only the counts are invalidated, so the counter goes
    /// blank rather than describing results that no longer exist.
    /// </summary>
    public void MarkInactive()
    {
        Total = NoSearch;
        Selected = -1;
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _pc += value;
        remove => _pc -= value;
    }

    private PropertyChangedEventHandler? _pc;

    private void Notify(string name) =>
        _pc?.Invoke(this, new PropertyChangedEventArgs(name));
}
