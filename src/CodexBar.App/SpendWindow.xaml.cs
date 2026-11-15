using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexBar.Core;

namespace CodexBar.App;

/// <summary>
/// The detail behind the popup's cost lines: a daily spend chart and per-model, per-project and token-mix
/// breakdowns for each provider that has local session logs.
/// </summary>
public partial class SpendWindow : Window
{
    private const int MaxRows = 8;
    private const double ChartWidth = 550;

    private readonly Func<IReadOnlyList<(string Name, CostSummary Cost)>> source;
    private string? selected;

    /// <param name="source">Returns the current cost summaries, one per provider that has any.</param>
    public SpendWindow(Func<IReadOnlyList<(string Name, CostSummary Cost)>> source)
    {
        this.source = source;
        this.InitializeComponent();
        this.Reload();
    }

    /// <summary>Redraws from the latest scan; called whenever the store reports a change.</summary>
    public void Reload()
    {
        var providers = this.source().Where(entry => entry.Cost.Report is not null).ToList();
        this.EmptyText.Visibility = providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.Body.Visibility = providers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        this.ProviderTabs.Children.Clear();
        if (providers.Count == 0)
        {
            return;
        }

        if (providers.All(entry => entry.Name != this.selected))
        {
            // Open on the provider with the most spend.
            this.selected = providers.MaxBy(entry => entry.Cost.MonthCost).Name;
        }

        foreach (var (name, _) in providers)
        {
            var tab = new Button
            {
                Content = name,
                Style = (Style)this.FindResource("FlatButton"),
                Margin = new Thickness(6, 0, 0, 0),
                FontWeight = name == this.selected ? FontWeights.SemiBold : FontWeights.Normal,
                Background = name == this.selected ? this.Res("Track") : Brushes.Transparent,
            };
            tab.Click += (_, _) =>
            {
                this.selected = name;
                this.Reload();
            };
            this.ProviderTabs.Children.Add(tab);
        }

        this.ProviderTabs.Visibility = providers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        this.Draw(providers.First(entry => entry.Name == this.selected).Cost);
    }

    private static string Money(double amount) => UsageFormatter.Money(amount, "USD");

    private static string Tokens(long count) => count switch
    {
        >= 1_000_000_000 => (count / 1_000_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + "B",
        >= 1_000_000 => (count / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 1_000 => (count / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "K",
        _ => count.ToString(CultureInfo.CurrentCulture),
    };

    private Brush Res(string key) => (Brush)this.FindResource(key);

    private void Draw(CostSummary cost)
    {
        var report = cost.Report!;
        var week = report.Days.TakeLast(7).Sum(day => day.Cost);
        var suffix = cost.HasUnpricedModels ? "+" : string.Empty;

        this.Tiles.Children.Clear();
        this.AddTile("Today", Money(cost.TodayCost) + suffix, Tokens(cost.TodayTokens) + " tokens");
        this.AddTile("Last 7 days", Money(week) + suffix, Money(week / 7) + " a day");
        this.AddTile("Last 30 days", Money(cost.MonthCost) + suffix, Tokens(cost.MonthTokens) + " tokens");
        this.AddTile("Requests", report.Requests.ToString("N0", CultureInfo.CurrentCulture), $"{report.LongContextRequests:N0} long-context");

        this.DrawChart(report.Days);
        this.FillRows(this.ModelRows, report.Models);
        this.FillRows(this.ProjectRows, report.Projects);
        this.DrawMix(report);

        this.Footnote.Text =
            "Estimated at API list prices from the session logs on this PC. "
            + (report.LongContextRequests > 0
                ? $"{report.LongContextRequests:N0} requests were long enough to be priced at the higher long-context rate. "
                : string.Empty)
            + (cost.HasUnpricedModels ? "A + means some usage is from models with no known price and is not counted. " : string.Empty)
            + "This is the value of the usage, not what a subscription charges.";
    }

    private void AddTile(string label, string value, string caption)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = this.Res("FgDim") });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = this.Res("FgDim"), Margin = new Thickness(0, 2, 0, 0) });
        this.Tiles.Children.Add(new Border
        {
            Background = this.Res("Card"),
            BorderBrush = this.Res("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(4, 0, 4, 0),
            Child = panel,
        });
    }

    /// <summary>One bar per day, scaled to the busiest day, with three light gridlines and the peak labelled.</summary>
    private void DrawChart(IReadOnlyList<DailyCost> days)
    {
        this.Chart.Children.Clear();
        this.Chart.Width = ChartWidth;
        var height = this.Chart.Height;
        const double top = 16;
        var plot = height - top;
        var peak = Math.Max(days.Max(day => day.Cost), 0.01);

        for (var line = 0; line <= 2; line++)
        {
            var y = top + (plot * line / 2);
            this.Chart.Children.Add(new Line
            {
                X1 = 0,
                X2 = ChartWidth,
                Y1 = y,
                Y2 = y,
                Stroke = this.Res("Line"),
                StrokeThickness = 1,
                SnapsToDevicePixels = true,
            });
        }

        var label = new TextBlock { Text = Money(peak), FontSize = 10, Foreground = this.Res("FgDim") };
        Canvas.SetLeft(label, 0);
        Canvas.SetTop(label, 0);
        this.Chart.Children.Add(label);

        var slot = ChartWidth / days.Count;
        var barWidth = Math.Max(2, slot - 4);
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var barHeight = day.Cost <= 0 ? 0 : Math.Max(2, plot * day.Cost / peak);
            var isToday = index == days.Count - 1;
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = this.Res("Accent"),
                Opacity = isToday ? 1 : 0.62,
            };
            Canvas.SetLeft(bar, (index * slot) + ((slot - barWidth) / 2));
            Canvas.SetTop(bar, height - barHeight);
            this.Chart.Children.Add(bar);

            // A full-height transparent strip, so even a day with no bar has a tooltip.
            var hit = new Rectangle
            {
                Width = slot,
                Height = height,
                Fill = Brushes.Transparent,
                ToolTip = $"{day.Day.ToString("ddd, MMM d", CultureInfo.CurrentCulture)}: {Money(day.Cost)} · {Tokens(day.Tokens)} tokens",
            };
            Canvas.SetLeft(hit, index * slot);
            this.Chart.Children.Add(hit);
        }

        this.ChartStart.Text = days[0].Day.ToString("MMM d", CultureInfo.CurrentCulture);
        this.ChartEnd.Text = "Today";
    }

    /// <summary>The top rows by cost, each with a bar relative to the largest; the rest are folded into one line.</summary>
    private void FillRows(StackPanel panel, IReadOnlyList<CostBreakdown> rows)
    {
        panel.Children.Clear();
        var shown = rows.Take(MaxRows).ToList();
        var largest = Math.Max(shown.Count == 0 ? 0 : shown.Max(row => row.Cost), 0.000001);
        foreach (var row in shown)
        {
            var header = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var amount = new TextBlock
            {
                Text = $"{Money(row.Cost)}{(row.HasUnpricedModels ? "+" : string.Empty)} · {Tokens(row.Tokens)} tokens · {row.Requests:N0} requests",
                FontSize = 12,
                Foreground = this.Res("FgDim"),
            };
            DockPanel.SetDock(amount, Dock.Right);
            header.Children.Add(amount);
            header.Children.Add(new TextBlock { Text = row.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(header);
            panel.Children.Add(new ProgressBar
            {
                Style = (Style)this.FindResource("Meter"),
                Height = 5,
                Margin = new Thickness(0, 4, 0, 0),
                Value = Math.Clamp(row.Cost / largest * 100, 0, 100),
                Foreground = this.Res("Accent"),
            });
        }

        if (rows.Count > shown.Count)
        {
            var rest = rows.Skip(shown.Count).ToList();
            panel.Children.Add(new TextBlock
            {
                Text = $"{rest.Count} more: {Money(rest.Sum(row => row.Cost))} · {Tokens(rest.Sum(row => row.Tokens))} tokens",
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = this.Res("FgDim"),
            });
        }

        if (rows.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Nothing in the last 30 days.", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = this.Res("FgDim") });
        }
    }

    /// <summary>A single stacked bar showing how the tokens divide between input, cache and output.</summary>
    private void DrawMix(CostReport report)
    {
        (string Label, long Count, Brush Fill)[] parts =
        [
            ("Input", report.InputTokens, this.Res("Accent")),
            ("Cache read", report.CacheReadTokens, this.Res("FgDim")),
            ("Cache write", report.CacheWriteTokens, this.Res("Warn")),
            ("Output", report.OutputTokens, this.Res("Danger")),
        ];
        var total = Math.Max(1, parts.Sum(part => part.Count));

        this.MixBar.Children.Clear();
        this.MixBar.ColumnDefinitions.Clear();
        this.MixLegend.Children.Clear();
        foreach (var (label, count, fill) in parts)
        {
            if (count <= 0)
            {
                continue;
            }

            this.MixBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
            var segment = new Rectangle { Fill = fill, Margin = new Thickness(0, 0, 1, 0) };
            Grid.SetColumn(segment, this.MixBar.ColumnDefinitions.Count - 1);
            this.MixBar.Children.Add(segment);

            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            legend.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = fill, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock
            {
                Text = $"{label} {Tokens(count)} ({count * 100d / total:0.#}%)",
                FontSize = 12,
                Foreground = this.Res("FgDim"),
            });
            this.MixLegend.Children.Add(legend);
        }
    }
}
