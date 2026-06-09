using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Windows;

public sealed class AncestorScrollViewerScopeTests
{
    // The walk is nearest-first: index 0 is the element's immediate
    // parent, larger indices are farther ancestors. The app content
    // root (Window.Content) sits high in the chain; the parasitic
    // framework ScrollViewer is its ancestor (index >= rootIndex).
    // Legitimate ScrollViewers (settings, tab strips) are descendants
    // of the app root (index < rootIndex) and must be left alone.

    [Fact]
    public void AppRootMissing_NeutersEverything()
    {
        // rootIndex < 0 means the app root was not seen in the walk;
        // preserve the original single-pane behaviour (neuter all).
        Assert.True(AncestorScrollViewerScope.InScope(index: 0, rootIndex: -1));
        Assert.True(AncestorScrollViewerScope.InScope(index: 5, rootIndex: -1));
    }

    [Fact]
    public void BelowAppRoot_IsLegitimate_NotInScope()
    {
        // Indices before the app root are descendants of it = legitimate.
        Assert.False(AncestorScrollViewerScope.InScope(index: 0, rootIndex: 3));
        Assert.False(AncestorScrollViewerScope.InScope(index: 2, rootIndex: 3));
    }

    [Fact]
    public void AtOrAboveAppRoot_IsParasitic_InScope()
    {
        Assert.True(AncestorScrollViewerScope.InScope(index: 3, rootIndex: 3));
        Assert.True(AncestorScrollViewerScope.InScope(index: 4, rootIndex: 3));
    }
}
