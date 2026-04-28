using System;
using System.Collections.Generic;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;

namespace Ghostty.Tabs;

/// <summary>
/// XAML-side wrapper around <see cref="NewTabFlyoutController"/>.
/// Owns the per-window controller lifetime and exposes its row list to
/// <see cref="NewTabSplitButton"/>. Per-row icon resolution is deferred
/// pending an async-safe SetSourceAsync flow (the prior implementation
/// disposed the source MemoryStream synchronously, racing the async load).
/// </summary>
internal sealed class NewTabSplitButtonViewModel : IDisposable
{
    private readonly NewTabFlyoutController _controller;

    public NewTabSplitButtonViewModel(IProfileRegistry registry)
    {
        _controller = new NewTabFlyoutController(registry);
    }

    public IReadOnlyList<NewTabFlyoutController.Row> Rows => _controller.Rows;

    public void Dispose() => _controller.Dispose();
}
