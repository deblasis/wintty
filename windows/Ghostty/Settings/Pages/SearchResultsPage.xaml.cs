using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Settings.Pages;

internal sealed partial class SearchResultsPage : Page
{
    private string _query = string.Empty;
    private Action<string>? _onChosen;
    private Action? _onClear;

    public SearchResultsPage()
    {
        InitializeComponent();
    }

    public void Show(
        string query,
        IReadOnlyList<SearchHit> hits,
        Action<string> onChosen,
        Action onClear)
    {
        _query = query;
        _onChosen = onChosen;
        _onClear = onClear;

        TitleText.Text = hits.Count == 0
            ? "Search"
            : $"{hits.Count} result{(hits.Count == 1 ? "" : "s")} for \"{query}\"";

        ResultsPanel.Children.Clear();

        if (hits.Count == 0)
        {
            EmptyText.Text = $"No settings match \"{query}\".";
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
        BuildGroupedResults(hits);
    }

    private void BuildGroupedResults(IReadOnlyList<SearchHit> hits)
    {
        // Group by Page then Section. Ordering comes from the first
        // hit seen in each bucket, which preserves the scorer's
        // tier-desc sort at the top level -- the best page for the
        // query floats to the top of the results.
        var byPage = new List<(string Page, List<(string Section, List<SearchHit> Hits)> Sections)>();
        foreach (var hit in hits)
        {
            var pageBucket = byPage.FirstOrDefault(p => p.Page == hit.Entry.Page);
            if (pageBucket.Page == null)
            {
                pageBucket = (hit.Entry.Page, new List<(string, List<SearchHit>)>());
                byPage.Add(pageBucket);
            }
            var sectionBucket = pageBucket.Sections.FirstOrDefault(s => s.Section == hit.Entry.Section);
            if (sectionBucket.Section == null)
            {
                sectionBucket = (hit.Entry.Section, new List<SearchHit>());
                pageBucket.Sections.Add(sectionBucket);
            }
            sectionBucket.Hits.Add(hit);
        }

        foreach (var (page, sections) in byPage)
        {
            ResultsPanel.Children.Add(new TextBlock
            {
                Text = page,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                FontSize = 16,
                Margin = new Thickness(0, 12, 0, 4),
            });
            foreach (var (section, sectionHits) in sections)
            {
                ResultsPanel.Children.Add(new TextBlock
                {
                    Text = section,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(0, 4, 0, 4),
                });
                foreach (var hit in sectionHits)
                {
                    ResultsPanel.Children.Add(BuildRow(hit));
                }
            }
        }
    }

    private UIElement BuildRow(SearchHit hit)
    {
        // Rows are buttons so keyboard focus + Enter work out of the
        // box. The row itself is a Border inside the Button content;
        // ButtonPadding="0" keeps the visual padding on the Border.
        var header = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        AppendHighlighted(header.Inlines, hit.Entry.Label, _query);

        var description = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        AppendHighlighted(description.Inlines, hit.Entry.Description, _query);

        var breadcrumb = new TextBlock
        {
            Text = $"{hit.Entry.Page} > {hit.Entry.Section}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Margin = new Thickness(0, 4, 0, 0),
        };

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(header);
        panel.Children.Add(description);
        panel.Children.Add(breadcrumb);

        var border = new Border
        {
            Padding = new Thickness(16, 10, 16, 10),
            MinHeight = 68,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 6),
            Child = panel,
        };

        var button = new Button
        {
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = border,
        };
        button.Click += (_, _) => _onChosen?.Invoke(hit.Entry.Key);
        ToolTipService.SetToolTip(button, $"Open in {hit.Entry.Page}");
        return button;
    }

    // Split `text` around each case-insensitive occurrence of `query`
    // and render matches bolded with accent foreground. Inline spans
    // avoid re-measuring a child TextBlock for every match.
    private static void AppendHighlighted(InlineCollection inlines, string text, string query)
    {
        inlines.Clear();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            inlines.Add(new Run { Text = text });
            return;
        }

        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        int cursor = 0;
        while (cursor < text.Length)
        {
            var idx = text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                inlines.Add(new Run { Text = text[cursor..] });
                return;
            }
            if (idx > cursor)
                inlines.Add(new Run { Text = text[cursor..idx] });
            inlines.Add(new Run
            {
                Text = text.Substring(idx, query.Length),
                Foreground = accent,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            cursor = idx + query.Length;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _onClear?.Invoke();
}
