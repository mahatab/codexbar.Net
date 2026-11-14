using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexBar.Core;
using CodexBar.Core.Providers;

namespace CodexBar.App;

public partial class SettingsWindow : Window
{
    private static readonly (string Label, int Seconds)[] Intervals =
    [
        ("1 minute", 60), ("2 minutes", 120), ("5 minutes", 300), ("15 minutes", 900), ("30 minutes", 1800),
    ];

    private readonly UsageStore store;
    private readonly ConfigStore config;
    private readonly HttpClient http;
    private readonly Action changed;
    private readonly CancellationTokenSource lifetime = new();
    private bool loading = true;

    public SettingsWindow(UsageStore store, ConfigStore config, HttpClient http, string configPath, Action changed)
    {
        this.store = store;
        this.config = config;
        this.http = http;
        this.changed = changed;
        this.InitializeComponent();

        foreach (var (label, _) in Intervals)
        {
            this.IntervalBox.Items.Add(label);
        }

        var interval = Array.FindIndex(Intervals, entry => entry.Seconds >= config.RefreshSeconds);
        this.IntervalBox.SelectedIndex = interval < 0 ? Intervals.Length - 1 : interval;
        this.StartupBox.IsChecked = StartupRegistration.IsEnabled;

        var current = config.Current;
        this.TrayProviderBox.Items.Add("Highest usage (automatic)");
        foreach (var provider in store.Providers)
        {
            this.TrayProviderBox.Items.Add(provider.DisplayName);
        }

        var pinned = store.Providers.ToList().FindIndex(provider => provider.Id == current.TrayProvider);
        this.TrayProviderBox.SelectedIndex = pinned + 1;
        this.TrayPerProviderBox.IsChecked = current.TrayIconPerProvider;
        this.StatusBox.IsChecked = current.CheckStatus;
        this.PaceBox.IsChecked = current.ShowPace;
        this.HistoryBox.IsChecked = current.ShowHistory;
        this.CostBox.IsChecked = current.ShowCost;
        this.NotifyBox.IsChecked = current.NotifyHighUsage;
        this.ThresholdsBox.Text = FormatThresholds(config.AlertThresholds);
        this.NotifyResetBox.IsChecked = current.NotifyReset;
        this.NotifySignInBox.IsChecked = current.NotifySignInExpired;
        this.RefreshTokensBox.IsChecked = current.RefreshTokens;
        this.OutageHubBox.IsChecked = current.OutageHub;
        this.NotifyOutagesBox.IsChecked = current.NotifyOutages;
        this.CustomPagesBox.Text = string.Join(Environment.NewLine, current.CustomStatusPages);
        this.BuildOutageSources();
        this.ConfigPath.Text = $"Settings file: {configPath}";
        if (config.LoadError is { } error)
        {
            this.ConfigWarning.Text = error + " Changes made here last until CodexBar quits; fix or delete the file to save them.";
            this.ConfigWarning.Visibility = Visibility.Visible;
        }

        this.BuildProviders();
        this.loading = false;
        this.Closed += (_, _) => this.lifetime.Cancel();
    }

    private void BuildProviders()
    {
        this.ProviderList.Children.Clear();
        foreach (var provider in this.store.Providers)
        {
            this.ProviderList.Children.Add(this.BuildProviderCard(provider));
        }
    }

    private Border BuildProviderCard(IUsageProvider provider)
    {
        var panel = new StackPanel();
        var enabled = new CheckBox
        {
            Content = provider.DisplayName,
            FontWeight = FontWeights.SemiBold,
            IsChecked = this.store.IsEnabled(provider),
            Foreground = (Brush)this.FindResource("Fg"),
        };
        enabled.Checked += (_, _) => this.SetEnabled(provider, true);
        enabled.Unchecked += (_, _) => this.SetEnabled(provider, false);
        panel.Children.Add(enabled);

        var found = this.store.HasCredentials(provider);
        panel.Children.Add(new TextBlock
        {
            Text = (found ? "Credentials found. " : "Not set up yet. ") + provider.SetupHint,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)this.FindResource("FgDim"),
        });

        if (provider.UsesApiKey)
        {
            panel.Children.Add(this.BuildApiKeyRow(provider));
        }
        else if (provider is CopilotProvider)
        {
            panel.Children.Add(this.BuildCopilotRow(provider, found));
        }

        return new Border
        {
            Background = (Brush)this.FindResource("Card"),
            BorderBrush = (Brush)this.FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
    }

    private DockPanel BuildApiKeyRow(IUsageProvider provider)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var stored = this.config.GetSecret(provider.Id) is not null;
        var save = new Button { Content = "Save",Style = (Style)this.FindResource("FlatButton"), Margin = new Thickness(6, 0, 0, 0) };
        var clear = new Button
        {
            Content = "Remove",
            Style = (Style)this.FindResource("FlatButton"),
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = stored ? Visibility.Visible : Visibility.Collapsed,
        };
        // The saved key is never shown again; the box starts empty.
        var box = new PasswordBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = stored ? "A value is saved. Type a new one to replace it." : provider.SecretPrompt,
        };
        var prompt = new TextBlock
        {
            Text = stored ? "Saved. Type a new value to replace it." : provider.SecretPrompt,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = (Brush)this.FindResource("FgDim"),
        };
        DockPanel.SetDock(prompt, Dock.Top);
        row.Children.Add(prompt);
        save.Click += (_, _) =>
        {
            if (box.Password.Trim().Length == 0)
            {
                return;
            }

            this.config.SetSecret(provider.Id, box.Password);
            this.Reload();
        };
        clear.Click += (_, _) =>
        {
            this.config.SetSecret(provider.Id, null);
            this.Reload();
        };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(save);
        row.Children.Add(box);
        return row;
    }

    private StackPanel BuildCopilotRow(IUsageProvider provider, bool signedIn)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (Brush)this.FindResource("Fg"),
            Visibility = Visibility.Collapsed,
        };
        var button = new Button
        {
            Content = signedIn ? "Sign out of GitHub" : "Sign in with GitHub…",
            Style = (Style)this.FindResource("FlatButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += async (_, _) =>
        {
            if (signedIn)
            {
                this.config.SetSecret(provider.Id, null);
                this.Reload();
                return;
            }

            button.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            try
            {
                var flow = new CopilotDeviceFlow(this.http);
                var code = await flow.RequestDeviceCodeAsync(this.lifetime.Token);
                Clipboard.SetText(code.UserCode);
                status.Text = $"Enter code {code.UserCode} on the GitHub page that just opened (it is copied to your clipboard). Waiting for approval…";
                Shell.OpenUrl(code.VerificationUrl);
                var token = await flow.PollForTokenAsync(code, this.lifetime.Token);
                this.config.SetSecret(provider.Id, token);
                this.config.Update(config => config.Providers[provider.Id] = true);
                this.Reload();
            }
            catch (OperationCanceledException)
            {
                // Settings window closed while waiting.
            }
            catch (ProviderException error)
            {
                status.Text = error.Message;
                status.Foreground = (Brush)this.FindResource("Warn");
                button.IsEnabled = true;
            }
        };
        panel.Children.Add(button);
        panel.Children.Add(status);
        return panel;
    }

    private void SetEnabled(IUsageProvider provider, bool enabled)
    {
        this.config.Update(config => config.Providers[provider.Id] = enabled);
        this.changed();
    }

    private void Reload()
    {
        this.BuildProviders();
        this.changed();
    }

    private void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.loading || this.IntervalBox.SelectedIndex < 0)
        {
            return;
        }

        var seconds = Intervals[this.IntervalBox.SelectedIndex].Seconds;
        this.config.Update(config => config.RefreshSeconds = seconds);
        this.changed();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (!this.loading)
        {
            StartupRegistration.Set(this.StartupBox.IsChecked == true);
        }
    }

    /// <summary>Saves every plain on/off option; they are cheap to write together.</summary>
    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (this.loading)
        {
            return;
        }

        this.config.Update(config =>
        {
            config.TrayIconPerProvider = this.TrayPerProviderBox.IsChecked == true;
            config.CheckStatus = this.StatusBox.IsChecked == true;
            config.ShowPace = this.PaceBox.IsChecked == true;
            config.ShowHistory = this.HistoryBox.IsChecked == true;
            config.ShowCost = this.CostBox.IsChecked == true;
            config.NotifyHighUsage = this.NotifyBox.IsChecked == true;
            config.NotifyReset = this.NotifyResetBox.IsChecked == true;
            config.NotifySignInExpired = this.NotifySignInBox.IsChecked == true;
            config.RefreshTokens = this.RefreshTokensBox.IsChecked == true;
            config.OutageHub = this.OutageHubBox.IsChecked == true;
            config.NotifyOutages = this.NotifyOutagesBox.IsChecked == true;
        });
        this.changed();
    }

    /// <summary>One switch per outage source, including any custom status pages.</summary>
    private void BuildOutageSources()
    {
        this.OutageSourceList.Children.Clear();
        var switches = this.config.Current.OutageSources;
        foreach (var source in this.config.AllOutageSources())
        {
            var box = new CheckBox
            {
                Content = source.Name,
                IsChecked = !switches.TryGetValue(source.Id, out var enabled) || enabled,
                Margin = new Thickness(0, 8, 18, 0),
                Foreground = (Brush)this.FindResource("Fg"),
            };
            void Save(bool value)
            {
                this.config.Update(config => config.OutageSources[source.Id] = value);
                this.changed();
            }

            box.Checked += (_, _) => Save(true);
            box.Unchecked += (_, _) => Save(false);
            this.OutageSourceList.Children.Add(box);
        }
    }

    private void OnCustomPagesChanged(object sender, RoutedEventArgs e)
    {
        // Keep only addresses that parse as https Statuspage sites, and echo them back normalized.
        var pages = this.CustomPagesBox.Text
            .Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(OutageHub.CustomSource)
            .OfType<OutageSource>()
            .DistinctBy(source => source.Id)
            .Select(source => source.PageUrl)
            .ToList();
        if (pages.SequenceEqual(this.config.Current.CustomStatusPages))
        {
            return;
        }

        this.config.Update(config => config.CustomStatusPages = pages);
        this.CustomPagesBox.Text = string.Join(Environment.NewLine, pages);
        this.BuildOutageSources();
        this.changed();
    }

    private void OnTrayProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.loading || this.TrayProviderBox.SelectedIndex < 0)
        {
            return;
        }

        var index = this.TrayProviderBox.SelectedIndex;
        var id = index == 0 ? "auto" : this.store.Providers[index - 1].Id;
        this.config.Update(config => config.TrayProvider = id);
        this.changed();
    }

    private void OnThresholdsChanged(object sender, RoutedEventArgs e)
    {
        var parsed = this.ThresholdsBox.Text
            .Split([',', ';', ' ', '%'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
            .Where(value => value is > 0 and <= 100)
            .Distinct()
            .Order()
            .ToList();
        this.config.Update(config => config.AlertThresholds = parsed.Count == 0 ? [90] : parsed);

        // Echo what was understood, so a typo is visible instead of silently ignored.
        this.ThresholdsBox.Text = FormatThresholds(this.config.AlertThresholds);
    }

    private static string FormatThresholds(IEnumerable<double> thresholds) =>
        string.Join(", ", thresholds.Select(value => value.ToString("0.#", CultureInfo.InvariantCulture)));
}
