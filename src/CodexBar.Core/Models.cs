namespace CodexBar.Core;

/// <summary>One quota lane, for example the 5-hour session or the weekly limit.</summary>
public sealed record RateWindow(
    string Id,
    string Title,
    double UsedPercent,
    int? WindowMinutes = null,
    DateTimeOffset? ResetsAt = null,
    string? Note = null)
{
    public double RemainingPercent => Math.Clamp(100 - this.UsedPercent, 0, 100);
}

/// <summary>A money or credit figure shown under the quota bars.</summary>
public sealed record DetailRow(string Label, string Value, double? Progress = null);

public sealed record UsageSnapshot(
    string ProviderId,
    IReadOnlyList<RateWindow> Windows,
    IReadOnlyList<DetailRow> Details,
    string? Plan,
    string? Account,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The most constrained window, used for the tray icon and tooltip.</summary>
    public RateWindow? MostConstrained =>
        this.Windows.Count == 0 ? null : this.Windows.MaxBy(window => window.UsedPercent);
}

public enum ProviderErrorKind
{
    /// <summary>No credentials found; the provider is simply not set up on this machine.</summary>
    NotConfigured,

    /// <summary>Credentials exist but were rejected or have expired.</summary>
    Unauthorized,

    RateLimited,
    Network,
    InvalidResponse,
    Server,
}

public sealed class ProviderException : Exception
{
    public ProviderException(ProviderErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        this.Kind = kind;
    }

    public ProviderErrorKind Kind { get; }
}

/// <summary>Latest known state of one provider: a snapshot, an error, or both (stale data plus error).</summary>
public sealed record ProviderState(
    string ProviderId,
    UsageSnapshot? Snapshot,
    string? Error,
    ProviderErrorKind? ErrorKind,
    bool IsRefreshing,
    ProviderStatus? Status = null,
    CostSummary? Cost = null)
{
    public static ProviderState Empty(string providerId) => new(providerId, null, null, null, false);
}

/// <summary>Service health as reported by the provider's public status page.</summary>
public enum StatusIndicator
{
    None,
    Maintenance,
    Minor,
    Major,
    Critical,
    Unknown,
}

public sealed record ProviderStatus(StatusIndicator Indicator, string? Description, DateTimeOffset? UpdatedAt)
{
    public bool IsIncident => this.Indicator is not (StatusIndicator.None or StatusIndicator.Unknown);
}

/// <summary>Token totals and API-list-price estimates from local session logs on this PC.</summary>
public sealed record CostSummary(
    long TodayTokens,
    double TodayCost,
    long MonthTokens,
    double MonthCost,
    bool HasUnpricedModels,
    DateTimeOffset ScannedAt);
