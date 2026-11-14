using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexBar.Core;

namespace CodexBar.App;

/// <summary>One quota bar in the popup.</summary>
public sealed record WindowRow(string Title, string PercentText, double Used, Brush Fill, string Caption, PointCollection? Trend)
{
    public const double TrendWidth = 54;
    public const double TrendHeight = 12;

    public Visibility CaptionVisibility => this.Caption.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TrendVisibility => this.Trend is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One service in the outage hub card.</summary>
public sealed record OutageRow(string Name, string State, string Detail, Brush Dot, Brush StateBrush, string Url, bool IsIncident)
{
    public Visibility DetailVisibility => this.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The hub's rows: AI providers that have a status reading, then the cloud sources. Services with an open
    /// incident come first, worst first, so the card answers "what is down?" at a glance.
    /// </summary>
    public static List<OutageRow> Build(
        IEnumerable<(IUsageProvider Provider, ProviderState State)> providers,
        IReadOnlyList<OutageReading> readings,
        ResourceDictionary resources)
    {
        var entries = new List<(string Name, ProviderStatus? Status, string Url)>();
        foreach (var (provider, state) in providers)
        {
            if (state.Status is not null && provider.StatusLinkUrl is { } link)
            {
                entries.Add((provider.DisplayName, state.Status, link));
            }
        }

        // GitHub's status already appears as Copilot when that provider is on.
        var shown = entries.Select(entry => entry.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        entries.AddRange(readings
            .Where(reading => !shown.Contains(reading.Source.PageUrl.TrimEnd('/')))
            .Select(reading => (reading.Source.Name, reading.Status, reading.Source.PageUrl)));

        return entries
            .Select((entry, index) => (entry, index))
            .OrderByDescending(item => item.entry.Status?.IsIncident == true ? (int)item.entry.Status.Indicator : 0)
            .ThenBy(item => item.index)
            .Select(item => From(item.entry.Name, item.entry.Status, item.entry.Url, resources))
            .ToList();
    }

    private static OutageRow From(string name, ProviderStatus? status, string url, ResourceDictionary resources)
    {
        var dim = (Brush)resources["FgDim"];
        if (status is null)
        {
            return new OutageRow(name, "Unknown", string.Empty, (Brush)resources["Track"], dim, url, false);
        }

        if (!status.IsIncident)
        {
            return new OutageRow(name, "Operational", string.Empty, (Brush)resources["Accent"], dim, url, false);
        }

        var brush = (Brush)resources[status.Indicator is StatusIndicator.Major or StatusIndicator.Critical ? "Danger" : "Warn"];
        return new OutageRow(name, StatusFetcher.Label(status.Indicator), status.Description ?? string.Empty, brush, brush, url, true);
    }
}

/// <summary>What the popup shows beyond the raw readings; mirrors the Settings switches.</summary>
public sealed record CardOptions(bool ShowPace, bool ShowHistory, bool ShowCost)
{
    public static CardOptions From(AppConfig config) => new(config.ShowPace, config.ShowHistory, config.ShowCost);
}

/// <summary>One provider card in the popup. Built fresh from store state on every change.</summary>
public sealed class ProviderCard
{
    public required string Name { get; init; }

    public required string DashboardUrl { get; init; }

    public string Plan { get; init; } = string.Empty;

    public string Account { get; init; } = string.Empty;

    public IReadOnlyList<WindowRow> Windows { get; init; } = [];

    public IReadOnlyList<DetailRow> Details { get; init; } = [];

    public string Message { get; init; } = string.Empty;

    public Brush MessageBrush { get; init; } = Brushes.Gray;

    public string Footer { get; init; } = string.Empty;

    /// <summary>Service-health line, shown only while the provider's status page reports an incident.</summary>
    public string Status { get; init; } = string.Empty;

    public Brush StatusBrush { get; init; } = Brushes.Gray;

    public string StatusUrl { get; init; } = string.Empty;

    public Visibility PlanVisibility => Show(this.Plan);

    public Visibility AccountVisibility => Show(this.Account);

    public Visibility MessageVisibility => Show(this.Message);

    public Visibility FooterVisibility => Show(this.Footer);

    public Visibility StatusVisibility => Show(this.Status);

    public static ProviderCard From(
        IUsageProvider provider,
        ProviderState state,
        DateTimeOffset now,
        ResourceDictionary resources,
        CardOptions options,
        HistoryStore? history = null)
    {
        var snapshot = state.Snapshot;
        var windows = (snapshot?.Windows ?? [])
            .Select(window => new WindowRow(
                window.Title,
                $"{UsageFormatter.Percent(window.UsedPercent)} used",
                window.UsedPercent,
                LevelBrush(window.UsedPercent, resources),
                string.Join(" · ", new[]
                    {
                        window.Note,
                        UsageFormatter.ResetCountdown(window.ResetsAt, now),
                        options.ShowPace ? Pace.Describe(window, now) : null,
                    }
                    .Where(part => !string.IsNullOrEmpty(part))),
                options.ShowHistory && history is not null ? Trend(history.Samples(provider.Id, window.Id), window, now) : null))
            .ToList();

        var details = (snapshot?.Details ?? []).ToList();
        if (options.ShowCost && state.Cost is { } cost)
        {
            var suffix = cost.HasUnpricedModels ? "+" : string.Empty;
            details.Add(new DetailRow("Today (est. API value)", $"{UsageFormatter.Money(cost.TodayCost, "USD")}{suffix} · {Tokens(cost.TodayTokens)}"));
            details.Add(new DetailRow("Last 30 days", $"{UsageFormatter.Money(cost.MonthCost, "USD")}{suffix} · {Tokens(cost.MonthTokens)}"));
        }

        string message;
        var messageBrush = (Brush)resources["Warn"];
        if (state.Error is not null)
        {
            message = state.Error;
        }
        else if (snapshot is null)
        {
            message = state.IsRefreshing ? "Loading…" : "Not refreshed yet.";
            messageBrush = (Brush)resources["FgDim"];
        }
        else if (windows.Count == 0 && snapshot.Details.Count == 0)
        {
            message = "No usage limits reported for this account.";
            messageBrush = (Brush)resources["FgDim"];
        }
        else
        {
            message = string.Empty;
        }

        var footer = snapshot is null ? string.Empty : UsageFormatter.UpdatedAgo(snapshot.UpdatedAt, now);
        if (snapshot is not null && state.Error is not null)
        {
            footer += " (last known)";
        }

        var status = string.Empty;
        var statusBrush = (Brush)resources["Warn"];
        if (state.Status is { IsIncident: true } health)
        {
            status = "● " + StatusFetcher.Label(health.Indicator)
                + (string.IsNullOrEmpty(health.Description) ? string.Empty : $": {health.Description}");
            statusBrush = (Brush)resources[health.Indicator is StatusIndicator.Major or StatusIndicator.Critical ? "Danger" : "Warn"];
        }

        return new ProviderCard
        {
            Name = provider.DisplayName,
            DashboardUrl = provider.DashboardUrl,
            Plan = snapshot?.Plan ?? string.Empty,
            Account = snapshot?.Account ?? string.Empty,
            Windows = windows,
            Details = details,
            Message = message,
            MessageBrush = messageBrush,
            Footer = footer,
            Status = status,
            StatusBrush = statusBrush,
            StatusUrl = provider.StatusLinkUrl ?? string.Empty,
        };
    }

    public static Brush LevelBrush(double usedPercent, ResourceDictionary resources) => (Brush)resources[
        usedPercent >= UsageStore.HighUsageThreshold ? "Danger" : usedPercent >= 70 ? "Warn" : "Accent"];

    /// <summary>
    /// Trend line points in device-independent pixels: the last day for short windows, the last week otherwise.
    /// Null until there are enough readings to draw a meaningful line.
    /// </summary>
    internal static PointCollection? Trend(IReadOnlyList<HistorySample> samples, RateWindow window, DateTimeOffset now)
    {
        var span = window.WindowMinutes is { } minutes && minutes < 1440 ? TimeSpan.FromDays(1) : HistoryStore.Retention;
        var end = now.ToUnixTimeSeconds();
        var start = end - (long)span.TotalSeconds;
        var recent = samples.Where(sample => sample.UnixSeconds >= start && sample.UnixSeconds <= end).ToList();
        // A few readings minutes apart would draw as a dot; wait until they cover a visible stretch of time.
        if (recent.Count < 3 || recent[^1].UnixSeconds - recent[0].UnixSeconds < span.TotalSeconds / 24)
        {
            return null;
        }

        var points = new PointCollection(recent.Count);
        foreach (var sample in recent)
        {
            var x = (sample.UnixSeconds - start) / span.TotalSeconds * WindowRow.TrendWidth;
            var y = WindowRow.TrendHeight - 1 - (Math.Clamp(sample.UsedPercent, 0, 100) / 100 * (WindowRow.TrendHeight - 2));
            points.Add(new Point(x, y));
        }

        points.Freeze();
        return points;
    }

    private static string Tokens(long count) => count switch
    {
        >= 1_000_000_000 => (count / 1_000_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "B tokens",
        >= 1_000_000 => (count / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M tokens",
        >= 1_000 => (count / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "K tokens",
        _ => count.ToString(CultureInfo.CurrentCulture) + " tokens",
    };

    private static Visibility Show(string text) => text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
