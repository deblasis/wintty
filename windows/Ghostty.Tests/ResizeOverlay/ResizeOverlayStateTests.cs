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

    // --- Declarative visibility (IsVisible / NotifyResize / Hide) ----------

    [Fact]
    public void IsVisible_defaults_false()
    {
        Assert.False(new ResizeOverlayState().IsVisible);
    }

    [Fact]
    public void NotifyResize_shows_and_returns_true_on_a_real_resize()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };

        var shown = state.NotifyResize(80, 24, allowShow: true);

        Assert.True(shown);
        Assert.True(state.IsVisible);
        Assert.Equal("80 " + (char)0x00D7 + " 24", state.SizeText);
    }

    [Fact]
    public void NotifyResize_tracks_size_but_stays_hidden_when_not_allowed()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };

        var shown = state.NotifyResize(100, 30, allowShow: false);

        Assert.False(shown);
        Assert.False(state.IsVisible);
        // Size still tracked so the label is correct the next time it shows.
        Assert.Equal(100, state.Columns);
        Assert.Equal(30, state.Rows);
    }

    [Fact]
    public void NotifyResize_does_not_show_when_grid_is_unchanged()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };

        Assert.True(state.NotifyResize(80, 24, allowShow: true));
        state.Hide();

        Assert.False(state.NotifyResize(80, 24, allowShow: true));
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void NotifyResize_never_mode_never_shows()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Never };

        Assert.False(state.NotifyResize(80, 24, allowShow: true));
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void NotifyResize_after_first_suppresses_initial_then_shows()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.AfterFirst };

        Assert.False(state.NotifyResize(80, 24, allowShow: true)); // baseline
        Assert.False(state.IsVisible);
        Assert.True(state.NotifyResize(100, 30, allowShow: true));  // real resize
        Assert.True(state.IsVisible);
    }

    [Fact]
    public void Hide_clears_IsVisible()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };
        state.NotifyResize(80, 24, allowShow: true);

        state.Hide();

        Assert.False(state.IsVisible);
    }

    [Fact]
    public void NotifyResize_with_unchanged_grid_does_not_hide_a_visible_pill()
    {
        // A duplicate size event (common during a drag) must not flip the pill
        // off; only Hide (the auto-hide timer) clears it.
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };
        state.NotifyResize(80, 24, allowShow: true);

        var shown = state.NotifyResize(80, 24, allowShow: true);

        Assert.False(shown);          // nothing new to pulse
        Assert.True(state.IsVisible); // but still visible until Hide
    }

    [Fact]
    public void NotifyResize_raises_PropertyChanged_for_IsVisible()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };
        var raised = Subscribe(state);

        state.NotifyResize(80, 24, allowShow: true);

        Assert.Contains(nameof(ResizeOverlayState.IsVisible), raised);
    }

    [Fact]
    public void Hide_raises_PropertyChanged_for_IsVisible_only_when_changing()
    {
        var state = new ResizeOverlayState { Mode = ResizeOverlayMode.Always };
        state.NotifyResize(80, 24, allowShow: true);
        var raised = Subscribe(state);

        state.Hide();
        state.Hide(); // idempotent: no second notification

        Assert.Single(raised, nameof(ResizeOverlayState.IsVisible));
    }

    private static List<string?> Subscribe(ResizeOverlayState state)
    {
        var raised = new List<string?>();
        ((INotifyPropertyChanged)state).PropertyChanged +=
            (_, e) => raised.Add(e.PropertyName);
        return raised;
    }
}
