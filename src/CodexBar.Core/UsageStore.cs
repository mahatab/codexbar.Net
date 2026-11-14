using CodexBar.Core.Providers;

namespace CodexBar.Core;

public enum AlertKind
{
    /// <summary>A quota window crossed one of the configured usage thresholds.</summary>
    Threshold,

    /// <summary>A quota window that was notably used has reset.</summary>
    Reset,

    /// <summary>A provider that was working now rejects its sign-in.</summary>
    SignInExpired,
}

public sealed record UsageAlert(AlertKind Kind, string ProviderName, RateWindow? Window = null, double? Threshold = null);

/// <summary>Owns the provider list and their latest states, and runs refreshes.</summary>
public sealed class UsageStore
{
    /// <summary>Usage at or above this is drawn in the danger color.</summary>
    public const double HighUsageThreshold = 90;

    private static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan CostInterval = TimeSpan.FromMinutes(10);

    private readonly ProviderContext context;
    private readonly ConfigStore config;
    private readonly CostScanner costScanner;
    private readonly object gate = new();
    private readonly Dictionary<string, ProviderState> states = [];
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim costGate = new(1, 1);
    private DateTimeOffset? statusCheckedAt;
    private DateTimeOffset? costScannedAt;

    public UsageStore(
        ProviderContext context,
        ConfigStore config,
        IReadOnlyList<IUsageProvider>? providers = null,
        HistoryStore? history = null,
        CostScanner? costScanner = null)
    {
        this.context = context;
        this.config = config;
        this.Providers = providers ?? DefaultProviders();
        this.History = history ?? new HistoryStore(null);
        this.costScanner = costScanner ?? new CostScanner();
    }

    /// <summary>Raised on a thread-pool thread whenever any provider state changes.</summary>
    public event Action? Changed;

    /// <summary>Raised for the notifications the user has switched on. Never raised by a provider's first reading.</summary>
    public event Action<UsageAlert>? Alert;

    public IReadOnlyList<IUsageProvider> Providers { get; }

    public HistoryStore History { get; }

    public DateTimeOffset Now => this.context.Clock();

    public static IReadOnlyList<IUsageProvider> DefaultProviders() =>
    [
        new CodexProvider(),
        new ClaudeProvider(),
        new CursorProvider(),
        new GeminiProvider(),
        new CopilotProvider(),
        new GrokProvider(),
        new OpenRouterProvider(),
    ];

    public bool HasCredentials(IUsageProvider provider) => provider.HasCredentials(this.context);

    /// <summary>An explicit switch wins; otherwise a provider is on when its credentials are found.</summary>
    public bool IsEnabled(IUsageProvider provider) =>
        this.config.ProviderSwitch(provider.Id) ?? provider.HasCredentials(this.context);

    public IReadOnlyList<IUsageProvider> EnabledProviders() => this.Providers.Where(this.IsEnabled).ToList();

    public ProviderState State(string providerId)
    {
        lock (this.gate)
        {
            return this.states.TryGetValue(providerId, out var state) ? state : ProviderState.Empty(providerId);
        }
    }

    /// <summary>Refreshes every enabled provider in parallel. Overlapping calls are coalesced.</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        if (!await this.refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var enabled = this.EnabledProviders();
            lock (this.gate)
            {
                foreach (var stale in this.states.Keys.Except(enabled.Select(p => p.Id)).ToList())
                {
                    this.states.Remove(stale);
                }
            }

            var work = enabled.Select(provider => this.RefreshAsync(provider, cancellationToken)).ToList();
            work.Add(this.RefreshStatusAsync(enabled, cancellationToken));
            await Task.WhenAll(work).ConfigureAwait(false);

            // The log scan can take a while on the first run; it must not hold up the next usage refresh.
            _ = this.RefreshCostsAsync(cancellationToken);
        }
        finally
        {
            this.refreshGate.Release();
            this.Changed?.Invoke();
        }
    }

    /// <summary>Rescans local Codex and Claude Code session logs, at most once per interval unless forced.</summary>
    public async Task RefreshCostsAsync(CancellationToken cancellationToken, bool force = false)
    {
        var now = this.Now;
        if (!this.config.Current.ShowCost)
        {
            this.ForEachState(state => state with { Cost = null });
            return;
        }

        // A forced scan waits for one already running; a routine one just skips this round.
        if (force)
        {
            await this.costGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await this.costGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!force && this.costScannedAt is { } last && now - last < CostInterval)
            {
                return;
            }

            var enabled = this.EnabledProviders().Select(provider => provider.Id).ToHashSet();
            if (this.costScanner.Catalog is { } catalog && (enabled.Contains("codex") || enabled.Contains("claude")))
            {
                await catalog.EnsureFreshAsync(this.context.Http, now, cancellationToken).ConfigureAwait(false);
            }

            await Task.Run(
                () =>
                {
                    if (enabled.Contains("codex"))
                    {
                        var root = Path.GetDirectoryName(CodexProvider.AuthFilePath(this.context))!;
                        this.SetCost("codex", this.costScanner.ScanCodex(root, now));
                    }

                    if (enabled.Contains("claude"))
                    {
                        var root = Path.GetDirectoryName(ClaudeProvider.CredentialsPath(this.context))!;
                        this.SetCost("claude", this.costScanner.ScanClaude(root, now));
                    }
                },
                cancellationToken).ConfigureAwait(false);
            this.costScannedAt = now;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            this.costGate.Release();
            this.Changed?.Invoke();
        }
    }

    private void SetCost(string providerId, CostSummary summary) =>
        this.Mutate(providerId, state => state with { Cost = summary.MonthTokens > 0 ? summary : null }, create: false);

    private async Task RefreshStatusAsync(IReadOnlyList<IUsageProvider> enabled, CancellationToken cancellationToken)
    {
        if (!this.config.Current.CheckStatus)
        {
            this.ForEachState(state => state with { Status = null });
            return;
        }

        var now = this.Now;
        if (this.statusCheckedAt is { } last && now - last < StatusInterval)
        {
            return;
        }

        this.statusCheckedAt = now;
        await Task.WhenAll(enabled.Select(async provider =>
        {
            var status = await StatusFetcher.FetchAsync(this.context.Http, provider, cancellationToken).ConfigureAwait(false);
            if (status is not null)
            {
                // A failed status check keeps the last known status instead of blanking it.
                this.Mutate(provider.Id, state => state with { Status = status }, create: true);
            }
        })).ConfigureAwait(false);
    }

    private async Task RefreshAsync(IUsageProvider provider, CancellationToken cancellationToken)
    {
        var previous = this.Mutate(provider.Id, state => state with { IsRefreshing = true }, create: true);
        try
        {
            var snapshot = await provider.FetchAsync(this.context, cancellationToken).ConfigureAwait(false);
            this.Mutate(
                provider.Id,
                state => state with { Snapshot = snapshot, Error = null, ErrorKind = null, IsRefreshing = false },
                create: true);
            this.History.Record(snapshot);
            this.RaiseUsageAlerts(provider, previous.Snapshot, snapshot);
        }
        catch (ProviderException error)
        {
            // Keep the last good reading visible as stale data, except when the provider is not set up at all.
            var keepSnapshot = error.Kind != ProviderErrorKind.NotConfigured;
            this.Mutate(
                provider.Id,
                state => state with
                {
                    Snapshot = keepSnapshot ? state.Snapshot : null,
                    Error = error.Message,
                    ErrorKind = error.Kind,
                    IsRefreshing = false,
                },
                create: true);
            if (error.Kind == ProviderErrorKind.Unauthorized
                && previous is { Snapshot: not null, ErrorKind: not ProviderErrorKind.Unauthorized }
                && this.config.Current.NotifySignInExpired)
            {
                this.Alert?.Invoke(new UsageAlert(AlertKind.SignInExpired, provider.DisplayName));
            }
        }
        catch (OperationCanceledException)
        {
            this.Mutate(provider.Id, state => state with { IsRefreshing = false }, create: true);
        }
        catch (Exception error)
        {
            this.Mutate(
                provider.Id,
                state => state with
                {
                    Error = $"{provider.DisplayName} refresh failed: {error.Message}",
                    ErrorKind = ProviderErrorKind.InvalidResponse,
                    IsRefreshing = false,
                },
                create: true);
        }
    }

    private void RaiseUsageAlerts(IUsageProvider provider, UsageSnapshot? before, UsageSnapshot after)
    {
        if (before is null)
        {
            // The first reading sets the baseline; alerting on launch would be noise.
            return;
        }

        var settings = this.config.Current;
        var thresholds = this.config.AlertThresholds;
        foreach (var window in after.Windows)
        {
            if (before.Windows.FirstOrDefault(w => w.Id == window.Id) is not { } earlier)
            {
                continue;
            }

            // Several thresholds crossed in one step produce a single alert, for the highest one.
            var crossed = thresholds.LastOrDefault(t => earlier.UsedPercent < t && window.UsedPercent >= t, double.NaN);
            if (settings.NotifyHighUsage && !double.IsNaN(crossed))
            {
                this.Alert?.Invoke(new UsageAlert(AlertKind.Threshold, provider.DisplayName, window, crossed));
            }

            var resetMoved = earlier.ResetsAt is { } oldReset && window.ResetsAt is { } newReset
                && newReset - oldReset > TimeSpan.FromMinutes(1);
            if (settings.NotifyReset && resetMoved && earlier.UsedPercent >= thresholds[0]
                && window.UsedPercent < earlier.UsedPercent)
            {
                this.Alert?.Invoke(new UsageAlert(AlertKind.Reset, provider.DisplayName, window));
            }
        }
    }

    /// <summary>Applies a change under the lock and returns the state as it was before the change.</summary>
    private ProviderState Mutate(string providerId, Func<ProviderState, ProviderState> change, bool create)
    {
        ProviderState before;
        lock (this.gate)
        {
            if (!this.states.TryGetValue(providerId, out var current))
            {
                if (!create)
                {
                    return ProviderState.Empty(providerId);
                }

                current = ProviderState.Empty(providerId);
            }

            before = current;
            this.states[providerId] = change(current);
        }

        this.Changed?.Invoke();
        return before;
    }

    private void ForEachState(Func<ProviderState, ProviderState> change)
    {
        lock (this.gate)
        {
            foreach (var id in this.states.Keys.ToList())
            {
                this.states[id] = change(this.states[id]);
            }
        }
    }
}
