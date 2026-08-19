using System.Collections.Generic;
using System.ComponentModel;
using Ghostty.Core.Search;
using Xunit;

namespace Ghostty.Tests.Search;

public class SearchStateTests
{
    [Fact]
    public void Default_state_is_closed_and_empty()
    {
        var state = new SearchState();

        Assert.False(state.IsOpen);
        Assert.Equal(string.Empty, state.Needle);
        Assert.Equal(0L, state.Total);
        Assert.Equal(-1L, state.Selected);
        Assert.Equal(string.Empty, state.CounterText);
    }

    [Fact]
    public void CounterText_is_empty_when_needle_is_empty()
    {
        var state = new SearchState
        {
            Total = 5,
            Selected = 0,
        };

        Assert.Equal(string.Empty, state.CounterText);
    }

    [Fact]
    public void CounterText_says_no_matches_when_total_is_zero()
    {
        var state = new SearchState { Needle = "foo" };

        Assert.Equal("No matches", state.CounterText);
    }

    [Fact]
    public void CounterText_formats_first_match_as_one_of_total()
    {
        var state = new SearchState
        {
            Needle = "foo",
            Total = 5,
            Selected = 0,
        };

        Assert.Equal("1 of 5", state.CounterText);
    }

    [Fact]
    public void CounterText_formats_last_match_as_total_of_total()
    {
        var state = new SearchState
        {
            Needle = "foo",
            Total = 5,
            Selected = 4,
        };

        Assert.Equal("5 of 5", state.CounterText);
    }

    [Fact]
    public void CounterText_formats_unselected_as_zero_of_total()
    {
        var state = new SearchState
        {
            Needle = "foo",
            Total = 5,
            Selected = -1,
        };

        Assert.Equal("0 of 5", state.CounterText);
    }

    // libghostty sends -1 for "no total" when the search thread quits, which
    // is exactly the state the bar is left in after a close. Treating it as a
    // count rendered "0 of -1" next to the needle.
    [Fact]
    public void CounterText_is_empty_when_total_is_negative()
    {
        var state = new SearchState
        {
            Needle = "foo",
            Total = -1,
            Selected = -1,
        };

        Assert.Equal(string.Empty, state.CounterText);
    }

    [Fact]
    public void CounterText_is_empty_when_total_is_negative_and_a_match_is_selected()
    {
        var state = new SearchState
        {
            Needle = "foo",
            Total = -1,
            Selected = 3,
        };

        Assert.Equal(string.Empty, state.CounterText);
    }

    [Fact]
    public void Setting_IsOpen_raises_PropertyChanged()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.IsOpen = true;

        Assert.Contains(nameof(SearchState.IsOpen), raised);
    }

    [Fact]
    public void Setting_Needle_raises_PropertyChanged()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.Needle = "hello";

        Assert.Contains(nameof(SearchState.Needle), raised);
    }

    [Fact]
    public void Setting_Total_raises_PropertyChanged()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.Total = 7;

        Assert.Contains(nameof(SearchState.Total), raised);
    }

    [Fact]
    public void Setting_Selected_raises_PropertyChanged()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.Selected = 3;

        Assert.Contains(nameof(SearchState.Selected), raised);
    }

    [Fact]
    public void Changing_Needle_also_raises_CounterText()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.Needle = "abc";

        Assert.Contains(nameof(SearchState.CounterText), raised);
    }

    [Fact]
    public void Changing_Total_also_raises_CounterText()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.Total = 10;

        Assert.Contains(nameof(SearchState.CounterText), raised);
    }

    [Fact]
    public void Changing_Selected_also_raises_CounterText()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.Selected = 2;

        Assert.Contains(nameof(SearchState.CounterText), raised);
    }

    [Fact]
    public void Changing_IsOpen_does_not_raise_CounterText()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.IsOpen = true;

        Assert.DoesNotContain(nameof(SearchState.CounterText), raised);
    }

    [Fact]
    public void Reset_restores_defaults()
    {
        var state = new SearchState
        {
            IsOpen = true,
            Needle = "foo",
            Total = 12,
            Selected = 4,
        };

        state.Reset();

        Assert.False(state.IsOpen);
        Assert.Equal(string.Empty, state.Needle);
        Assert.Equal(0L, state.Total);
        Assert.Equal(-1L, state.Selected);
        Assert.Equal(string.Empty, state.CounterText);
    }

    [Fact]
    public void Reset_raises_PropertyChanged_for_each_changed_field()
    {
        var state = new SearchState
        {
            IsOpen = true,
            Needle = "foo",
            Total = 12,
            Selected = 4,
        };
        var raised = Subscribe(state);

        state.Reset();

        Assert.Contains(nameof(SearchState.IsOpen), raised);
        Assert.Contains(nameof(SearchState.Needle), raised);
        Assert.Contains(nameof(SearchState.Total), raised);
        Assert.Contains(nameof(SearchState.Selected), raised);
        Assert.Contains(nameof(SearchState.CounterText), raised);
    }

    [Fact]
    public void Reset_from_defaults_does_not_raise_any_PropertyChanged()
    {
        var state = new SearchState();
        var raised = Subscribe(state);

        state.Reset();

        Assert.Empty(raised);
    }

    [Fact]
    public void Setting_IsOpen_to_current_value_does_not_raise()
    {
        var state = new SearchState { IsOpen = true };
        var raised = Subscribe(state);

        state.IsOpen = true;

        Assert.Empty(raised);
    }

    [Fact]
    public void Setting_Needle_to_current_value_does_not_raise()
    {
        var state = new SearchState { Needle = "abc" };
        var raised = Subscribe(state);

        state.Needle = "abc";

        Assert.Empty(raised);
    }

    [Fact]
    public void Setting_Total_to_current_value_does_not_raise()
    {
        var state = new SearchState { Total = 9 };
        var raised = Subscribe(state);

        state.Total = 9;

        Assert.Empty(raised);
    }

    [Fact]
    public void Setting_Selected_to_current_value_does_not_raise()
    {
        var state = new SearchState { Selected = 3 };
        var raised = Subscribe(state);

        state.Selected = 3;

        Assert.Empty(raised);
    }

    private static List<string?> Subscribe(SearchState state)
    {
        var raised = new List<string?>();
        ((INotifyPropertyChanged)state).PropertyChanged +=
            (_, e) => raised.Add(e.PropertyName);
        return raised;
    }
}
