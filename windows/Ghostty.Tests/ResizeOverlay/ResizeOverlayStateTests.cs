using System.Collections.Generic;
using System.ComponentModel;
using Ghostty.Core.ResizeOverlay;
using Xunit;

namespace Ghostty.Tests.ResizeOverlay;

public class ResizeOverlayStateTests
{
    [Fact]
    public void Default_state_is_zero_and_after_first()
    {
        var state = new ResizeOverlayState();

        Assert.Equal(0, state.Columns);
        Assert.Equal(0, state.Rows);
        Assert.Equal(ResizeOverlayMode.AfterFirst, state.Mode);
    }

    [Fact]
    public void SizeText_formats_columns_by_rows()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };

        state.ShouldPulse(80, 24);

        // U+00D7 MULTIPLICATION SIGN, built from its code point so the source stays ASCII.
        Assert.Equal("80 " + (char)0x00D7 + " 24", state.SizeText);
    }

    [Fact]
    public void ShouldPulse_updates_columns_and_rows()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };

        state.ShouldPulse(120, 40);

        Assert.Equal(120, state.Columns);
        Assert.Equal(40, state.Rows);
    }

    [Fact]
    public void Never_mode_never_pulses()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Never };

        Assert.False(state.ShouldPulse(80, 24));
        Assert.False(state.ShouldPulse(100, 30));
    }

    [Fact]
    public void Always_mode_pulses_on_first_size()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };

        Assert.True(state.ShouldPulse(80, 24));
    }

    [Fact]
    public void AfterFirst_suppresses_initial_layout_then_pulses()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.AfterFirst };

        Assert.False(state.ShouldPulse(80, 24));   // baseline
        Assert.True(state.ShouldPulse(100, 30));   // a real resize
    }

    [Fact]
    public void Identical_grid_does_not_repulse()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };

        Assert.True(state.ShouldPulse(80, 24));
        Assert.False(state.ShouldPulse(80, 24));  // unchanged grid
        Assert.True(state.ShouldPulse(80, 25));   // changed again
    }

    [Fact]
    public void ShouldPulse_raises_PropertyChanged_for_columns_rows_sizetext()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };
        var raised = Subscribe(state);

        state.ShouldPulse(80, 24);

        Assert.Contains(nameof(ResizeOverlayState.Columns), raised);
        Assert.Contains(nameof(ResizeOverlayState.Rows), raised);
        Assert.Contains(nameof(ResizeOverlayState.SizeText), raised);
    }

    private static List<string?> Subscribe(ResizeOverlayState state)
    {
        var raised = new List<string?>();
        ((INotifyPropertyChanged)state).PropertyChanged +=
            (_, e) => raised.Add(e.PropertyName);
        return raised;
    }
}
