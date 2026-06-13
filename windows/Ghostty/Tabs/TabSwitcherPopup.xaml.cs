using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// The transient Ctrl+Tab cycle popup. Stateless view: <see cref="Show"/> builds
/// the candidate row from a frozen snapshot; <see cref="Highlight"/> emphasizes
/// the current cursor cell. Driven by <see cref="TabSwitcherController"/>.
/// </summary>
internal sealed partial class TabSwitcherPopup : UserControl
{
    private readonly Dictionary<TabModel, Border> _cellByTab = new();

    // Cap each candidate title so a long title can't stretch the popup off-screen;
    // the title ellipsizes past this width.
    private const double TitleMaxWidth = 160;

    public TabSwitcherPopup() => InitializeComponent();

    public void Show(IReadOnlyList<TabModel> candidates)
    {
        CandidateRow.Children.Clear();
        _cellByTab.Clear();

        foreach (var tab in candidates)
        {
            var icon = new TabIconPresenter
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            icon.Attach(tab.TabIcon);

            var title = new TextBlock
            {
                Text = tab.EffectiveTitle,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = TitleMaxWidth,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(title);

            var cell = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Background = null,
                Child = row,
            };
            _cellByTab[tab] = cell;
            CandidateRow.Children.Add(cell);
        }
    }

    public void Highlight(TabModel tab)
    {
        foreach (var (model, cell) in _cellByTab)
        {
            cell.Background = ReferenceEquals(model, tab)
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : null;
        }
    }
}
