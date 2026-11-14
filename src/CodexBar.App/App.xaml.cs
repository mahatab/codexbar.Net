using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Core.Providers;
using Microsoft.Win32;

namespace CodexBar.App;

public partial class App : Application
{
    private const string MutexName = @"Local\CodexBar.Windows.SingleInstance";

    private readonly CancellationTokenSource lifetime = new();

    // Cookies are off so a response can never replace the session a provider chose to send.
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Tray icons keyed by provider id, or by "" for the single shared icon.</summary>
    private readonly Dictionary<string, TrayIcon> trays = [];
    private Mutex? instanceMutex;
    private ConfigStore config = null!;
    private UsageStore store = null!;
    private string configPath = string.Empty;
    private PopupWindow? popup;
    private SettingsWindow? settings;
    private DispatcherTimer? refreshTimer;
    private DispatcherTimer? clockTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply(this.Resources, Theme.AppsUseDark);

        var artIndex = Array.IndexOf(e.Args, "--art");
        if (artIndex >= 0 && artIndex + 1 < e.Args.Length)
        {
            // Writes the logo, the app icon and a Settings screenshot into a folder; used when preparing a release.
            try
            {
                var folder = e.Args[artIndex + 1];
                Directory.CreateDirectory(folder);
                Artwork.WriteLogo(Path.Combine(folder, "logo.png"), Path.Combine(folder, "app.ico"));
                Artwork.WriteTrayStates(Path.Combine(folder, "tray-icons.png"));
                var scratch = Path.Combine(Path.GetTempPath(), "codexbar-art-" + Guid.NewGuid().ToString("N"));
                var demoConfig = new ConfigStore(Path.Combine(scratch, "config.json"), new DpapiProtector());
                var demoContext = new ProviderContext(this.http, scratch);
                var demoStore = new UsageStore(demoContext, demoConfig);
                var window = new SettingsWindow(demoStore, demoConfig, this.http, @"%APPDATA%\CodexBar\config.json", () => { });
                Artwork.RenderWindow(window, Path.Combine(folder, "settings.png"));
                this.Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(e.Args[artIndex + 1], "art-failed.txt"), error.ToString());
                this.Shutdown(1);
            }

            return;
        }

        var renderIndex = Array.IndexOf(e.Args, "--render");
        if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
        {
            await this.RenderAndExitAsync(e.Args[renderIndex + 1], e.Args.Contains("--demo"), e.Args.Contains("--light"));
            return;
        }

        this.instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Another CodexBar is already in the notification area.
            this.Shutdown();
            return;
        }

        this.CreateStore();
        this.popup = new PopupWindow();
        this.popup.RefreshRequested += this.RefreshNow;
        this.popup.SettingsRequested += this.ShowSettings;
        this.popup.QuitRequested += this.Quit;

        this.store.Changed += () => this.Dispatcher.BeginInvoke(this.UpdateUi);
        this.store.Alert += alert => this.Dispatcher.BeginInvoke(() => this.ShowAlert(alert));

        this.refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(this.config.RefreshSeconds) };
        this.refreshTimer.Tick += (_, _) => this.RefreshNow();
        this.refreshTimer.Start();

        // Keeps reset countdowns, pace and "updated" ages current while the popup is open.
        this.clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        this.clockTimer.Tick += (_, _) =>
        {
            if (this.popup.IsVisible)
            {
                this.UpdateUi();
            }
        };
        this.clockTimer.Start();

        SystemEvents.UserPreferenceChanged += this.OnUserPreferenceChanged;
        this.UpdateUi();
        this.RefreshNow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= this.OnUserPreferenceChanged;
        this.lifetime.Cancel();
        foreach (var tray in this.trays.Values)
        {
            tray.Dispose();
        }

        this.http.Dispose();
        this.instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void CreateStore()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexBar");
        this.configPath = Path.Combine(folder, "config.json");
        this.config = new ConfigStore(this.configPath, new DpapiProtector());
        var context = new ProviderContext(
            this.http,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable,
            this.config.GetSecret,
            allowTokenRefresh: () => this.config.Current.RefreshTokens);
        this.store = new UsageStore(
            context,
            this.config,
            history: new HistoryStore(Path.Combine(folder, "history.json")),
            costScanner: new CostScanner(new PriceCatalog(Path.Combine(folder, "pricing.json"))));
    }

    private void RefreshNow() => _ = this.store.RefreshAllAsync(this.lifetime.Token);

    private void TogglePopup()
    {
        if (this.popup is null)
        {
            return;
        }

        if (this.popup.IsVisible)
        {
            this.popup.Hide();
        }
        else if (DateTime.UtcNow - this.popup.LastHidden > TimeSpan.FromMilliseconds(300))
        {
            // Clicking the tray icon deactivates an open popup first; without this guard that same click would reopen it.
            this.UpdateUi();
            this.popup.ShowNearTray();
        }
    }

    private void ShowSettings()
    {
        if (this.settings is { IsLoaded: true })
        {
            this.settings.Activate();
            return;
        }

        this.settings = new SettingsWindow(this.store, this.config, this.http, this.configPath, () =>
        {
            if (this.refreshTimer is not null)
            {
                this.refreshTimer.Interval = TimeSpan.FromSeconds(this.config.RefreshSeconds);
            }

            this.UpdateUi();
            this.RefreshNow();
        });
        this.settings.Show();
        this.settings.Activate();
    }

    private void Quit() => this.Shutdown();

    private void ShowAlert(UsageAlert alert)
    {
        var tray = this.trays.Values.FirstOrDefault();
        var window = alert.Window;
        switch (alert.Kind)
        {
            case AlertKind.Threshold when window is not null:
                tray?.Notify(
                    $"{alert.ProviderName} passed {UsageFormatter.Percent(alert.Threshold ?? window.UsedPercent)}",
                    $"{window.Title}: {UsageFormatter.Percent(window.UsedPercent)} used. "
                    + (UsageFormatter.ResetCountdown(window.ResetsAt, this.store.Now) ?? string.Empty));
                break;
            case AlertKind.Reset when window is not null:
                tray?.Notify(
                    $"{alert.ProviderName} limit reset",
                    $"{window.Title} is back to {UsageFormatter.Percent(window.UsedPercent)} used.",
                    warning: false);
                break;
            case AlertKind.Outage:
                tray?.Notify(
                    $"{alert.ProviderName} is reporting an incident",
                    string.IsNullOrEmpty(alert.Detail) ? "Open CodexBar for the outage hub." : alert.Detail);
                break;
            case AlertKind.SignInExpired:
                tray?.Notify(
                    $"{alert.ProviderName} sign-in expired",
                    $"CodexBar can no longer read {alert.ProviderName} usage. Open the popup for details.");
                break;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            this.Dispatcher.BeginInvoke(() =>
            {
                Theme.Apply(this.Resources, Theme.AppsUseDark);
                this.UpdateUi();
            });
        }
    }

    private void UpdateUi()
    {
        var now = this.store.Now;
        var enabled = this.store.EnabledProviders();
        var states = enabled.Select(provider => (Provider: provider, State: this.store.State(provider.Id))).ToList();
        var refreshing = states.Any(entry => entry.State.IsRefreshing);
        var options = CardOptions.From(this.config.Current);

        this.popup?.SetCards(
            states.Select(entry => ProviderCard.From(entry.Provider, entry.State, now, this.Resources, options, this.store.History)).ToList(),
            refreshing ? "Refreshing…" : $"Refreshes every {this.config.RefreshSeconds / 60} min",
            refreshing,
            this.config.Current.OutageHub ? OutageRow.Build(states, this.store.Outages, this.Resources) : null);
        this.UpdateTrays(states);
    }

    /// <summary>Keeps either one shared tray icon or one per enabled provider, as chosen in Settings.</summary>
    private void UpdateTrays(List<(IUsageProvider Provider, ProviderState State)> states)
    {
        var perProvider = this.config.Current.TrayIconPerProvider && states.Count > 0;
        var wanted = perProvider ? states.Select(entry => entry.Provider.Id).ToList() : [string.Empty];
        foreach (var key in this.trays.Keys.Except(wanted).ToList())
        {
            this.trays[key].Dispose();
            this.trays.Remove(key);
        }

        foreach (var key in wanted.Where(key => !this.trays.ContainsKey(key)))
        {
            this.trays[key] = new TrayIcon(this.TogglePopup, this.RefreshNow, this.ShowSettings, this.Quit);
        }

        if (perProvider)
        {
            foreach (var (provider, state) in states)
            {
                this.trays[provider.Id].Update(
                    Bars(state),
                    TooltipLine(provider, state),
                    NeedsAttention(state),
                    provider.DisplayName);
            }

            return;
        }

        var chosen = TrayProvider(states, this.config.Current.TrayProvider);
        this.trays[string.Empty].Update(
            chosen is null ? [] : Bars(chosen.Value.State),
            states.Count == 0
                ? "CodexBar: no providers enabled"
                : string.Join("\n", states.Select(entry => TooltipLine(entry.Provider, entry.State))),
            states.Any(entry => NeedsAttention(entry.State)));
    }

    /// <summary>The provider the shared icon follows: the configured one, else the one closest to a limit.</summary>
    internal static (IUsageProvider Provider, ProviderState State)? TrayProvider(
        List<(IUsageProvider Provider, ProviderState State)> states,
        string configured)
    {
        var withUsage = states.Where(entry => entry.State.Snapshot is { Windows.Count: > 0 }).ToList();
        if (withUsage.Count == 0)
        {
            return null;
        }

        var pinned = withUsage.FirstOrDefault(entry => entry.Provider.Id == configured);
        return pinned.Provider is not null
            ? pinned
            : withUsage.MaxBy(entry => entry.State.Snapshot!.MostConstrained!.UsedPercent);
    }

    private static List<double> Bars(ProviderState state) =>
        state.Snapshot?.Windows.Take(2).Select(window => window.UsedPercent).ToList() ?? [];

    private static bool NeedsAttention(ProviderState state) =>
        state.Error is not null || state.Status is { Indicator: StatusIndicator.Major or StatusIndicator.Critical };

    private static string TooltipLine(IUsageProvider provider, ProviderState state)
    {
        if (state.Snapshot?.MostConstrained is { } window)
        {
            return $"{provider.DisplayName}: {UsageFormatter.Percent(window.UsedPercent)} ({window.Title})";
        }

        return $"{provider.DisplayName}: {(state.Error is null ? "no limits" : "needs attention")}";
    }

    /// <summary>
    /// <c>--render out.png</c>: refresh once, write the popup as a PNG plus a text summary next to it, and exit.
    /// With <c>--demo</c> it uses made-up data and makes no network requests.
    /// </summary>
    private async Task RenderAndExitAsync(string path, bool demo, bool light)
    {
        try
        {
            if (light)
            {
                Theme.Apply(this.Resources, dark: false);
            }

            List<(IUsageProvider Provider, ProviderState State)> states;
            DateTimeOffset now;
            HistoryStore history;
            var options = new CardOptions(ShowPace: true, ShowHistory: true, ShowCost: true);
            List<OutageRow>? outageRows;
            var outageLog = new StringBuilder();
            if (demo)
            {
                now = DateTimeOffset.UtcNow;
                states = DemoData.States(now);
                history = DemoData.History(states, now);
                outageRows = OutageRow.Build(states, DemoData.Outages(now), this.Resources);
            }
            else
            {
                this.CreateStore();
                await this.store.RefreshAllAsync(this.lifetime.Token);
                await this.store.RefreshCostsAsync(this.lifetime.Token, force: true);
                now = this.store.Now;
                states = this.store.EnabledProviders().Select(p => (p, this.store.State(p.Id))).ToList();
                history = this.store.History;
                options = CardOptions.From(this.config.Current);
                outageRows = this.config.Current.OutageHub ? OutageRow.Build(states, this.store.Outages, this.Resources) : null;
                foreach (var reading in this.store.Outages)
                {
                    outageLog.AppendLine($"outage {reading.Source.Id}: {reading.Status?.Indicator.ToString() ?? "unknown"} {reading.Status?.Description}");
                }
            }

            if (Environment.GetCommandLineArgs().Contains("--hub-only"))
            {
                // For a screenshot of the outage hub card on its own.
                states = [];
            }

            var window = new PopupWindow();
            window.SetCards(
                states.Select(entry => ProviderCard.From(entry.Provider, entry.State, now, this.Resources, options, history)).ToList(),
                "Refreshes every 5 min",
                refreshing: false,
                outages: outageRows);
            window.RenderToPng(path);

            var summary = new StringBuilder();
            foreach (var (provider, state) in states)
            {
                summary.AppendLine($"{provider.Id}: error={state.Error ?? "none"} plan={state.Snapshot?.Plan ?? "-"} "
                    + $"status={state.Status?.Indicator.ToString() ?? "-"}");
                foreach (var row in state.Snapshot?.Windows ?? [])
                {
                    summary.AppendLine($"  {row.Title}: {row.UsedPercent:0.#}% used, resets {row.ResetsAt?.ToString("u") ?? "-"}, "
                        + $"pace={Pace.Describe(row, now) ?? "-"}");
                }

                foreach (var row in state.Snapshot?.Details ?? [])
                {
                    summary.AppendLine($"  {row.Label}: {row.Value}");
                }

                if (state.Cost is { } cost)
                {
                    summary.AppendLine($"  cost today=${cost.TodayCost:0.00} ({cost.TodayTokens} tokens) 30d=${cost.MonthCost:0.00} "
                        + $"({cost.MonthTokens} tokens) unpriced={cost.HasUnpricedModels}");
                }
            }

            File.WriteAllText(Path.ChangeExtension(path, ".txt"), summary.ToString() + outageLog);
            this.Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".txt"), "render failed: " + error);
            this.Shutdown(1);
        }
    }
}

/// <summary>Made-up readings for screenshots and UI checks; never touches real accounts.</summary>
internal static class DemoData
{
    public static List<(IUsageProvider Provider, ProviderState State)> States(DateTimeOffset now)
    {
        var codex = new UsageSnapshot(
            "codex",
            [
                new RateWindow("a", "Session (5h)", 38, 300, now.AddHours(2).AddMinutes(14)),
                new RateWindow("b", "Weekly", 74, 10080, now.AddDays(3).AddHours(5)),
            ],
            [new DetailRow("Credits", "1,250")],
            "Plus",
            "dev@example.com",
            now.AddMinutes(-2));
        var claude = new UsageSnapshot(
            "claude",
            [
                new RateWindow("a", "Session (5h)", 93, 300, now.AddMinutes(48)),
                new RateWindow("b", "Weekly", 51, 10080, now.AddDays(5)),
                new RateWindow("c", "Sonnet only · Weekly", 12, 10080, now.AddDays(5)),
            ],
            [new DetailRow("Extra usage", "$12.50 / $50.00", 0.25)],
            "Max 5x",
            null,
            now.AddSeconds(-20));
        var cursor = new UsageSnapshot(
            "cursor",
            [
                new RateWindow("a", "Plan usage · Monthly", 46, 43200, now.AddDays(12)),
                new RateWindow("b", "Auto models", 31, 43200, now.AddDays(12)),
                new RateWindow("c", "Named models", 61, 43200, now.AddDays(12)),
            ],
            [new DetailRow("On-demand", "$4.20 / $20.00", 0.21)],
            "Pro",
            "dev@example.com",
            now.AddMinutes(-1));
        var gemini = new UsageSnapshot(
            "gemini",
            [
                new RateWindow("a", "Pro models", 22, 1440, now.AddHours(9)),
                new RateWindow("b", "Flash models", 4, 1440, now.AddHours(9)),
            ],
            [],
            "Workspace",
            "dev@example.com",
            now.AddMinutes(-1));
        var grok = new UsageSnapshot(
            "grok",
            [new RateWindow("a", "Weekly", 17, 10080, now.AddDays(4).AddHours(2))],
            [new DetailRow("Grok Build", "14%"), new DetailRow("Grok Chat", "3%")],
            "SuperGrok",
            null,
            now.AddMinutes(-3));

        return
        [
            (new CodexProvider(), new ProviderState(
                "codex",
                codex,
                null,
                null,
                false,
                Cost: new CostSummary(1_840_000, 6.42, 61_300_000, 212.80, false, now))),
            (new ClaudeProvider(), new ProviderState(
                "claude",
                claude,
                null,
                null,
                false,
                new ProviderStatus(StatusIndicator.Minor, "Elevated error rates on some requests", now.AddMinutes(-12)),
                new CostSummary(9_200_000, 18.75, 244_000_000, 486.10, false, now))),
            (new CursorProvider(), new ProviderState("cursor", cursor, null, null, false)),
            (new GeminiProvider(), new ProviderState("gemini", gemini, null, null, false)),
            (new GrokProvider(), new ProviderState("grok", grok, null, null, false)),
        ];
    }

    /// <summary>Made-up outage hub readings: one incident, the rest healthy.</summary>
    public static List<OutageReading> Outages(DateTimeOffset now)
    {
        var sources = OutageHub.DefaultSources;
        return sources.Select(source => new OutageReading(source, source.Id switch
        {
            "aws" => new ProviderStatus(StatusIndicator.Major, "Amazon EC2, N. Virginia: Increased Error Rates", now.AddMinutes(-25)),
            "m365" => new ProviderStatus(StatusIndicator.Minor, "Outlook.com: Users may be unable to send email", now.AddMinutes(-40)),
            _ => new ProviderStatus(StatusIndicator.None, null, now),
        })).ToList();
    }

    /// <summary>A plausible week of readings ending at each window's current value, so trend lines can be drawn.</summary>
    public static HistoryStore History(List<(IUsageProvider Provider, ProviderState State)> states, DateTimeOffset now)
    {
        var history = new HistoryStore(null);
        const int steps = 60;
        foreach (var (_, state) in states)
        {
            if (state.Snapshot is not { } snapshot)
            {
                continue;
            }

            for (var step = steps; step >= 0; step--)
            {
                var age = step / (double)steps;
                history.Record(snapshot with
                {
                    UpdatedAt = now.AddHours(-160 * age),
                    Windows = snapshot.Windows.Select((window, index) => window with
                    {
                        // Rise toward the current value with a small ripple so the line is not perfectly straight.
                        UsedPercent = Math.Max(0, (window.UsedPercent * (1 - (age * age))) + (Math.Sin((step * 0.9) + index) * 2 * age)),
                    }).ToList(),
                });
            }
        }

        return history;
    }
}
