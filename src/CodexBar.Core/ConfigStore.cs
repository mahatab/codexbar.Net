using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBar.Core;

/// <summary>Encrypts secrets at rest. The Windows app supplies a DPAPI implementation.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <summary>Returns null when the value cannot be decrypted (for example, copied from another account).</summary>
    string? Unprotect(string protectedText);
}

public sealed class AppConfig
{
    public const int MinRefreshSeconds = 60;

    /// <summary>Per-provider switch. A missing entry means "on when credentials are found".</summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, bool> Providers { get; set; } = [];

    [JsonPropertyName("refreshSeconds")]
    public int RefreshSeconds { get; set; } = 300;

    [JsonPropertyName("notifyHighUsage")]
    public bool NotifyHighUsage { get; set; } = true;

    /// <summary>Usage percentages that trigger a notification when a window crosses them upward.</summary>
    [JsonPropertyName("alertThresholds")]
    public List<double> AlertThresholds { get; set; } = [90];

    [JsonPropertyName("notifyReset")]
    public bool NotifyReset { get; set; }

    [JsonPropertyName("notifySignInExpired")]
    public bool NotifySignInExpired { get; set; } = true;

    [JsonPropertyName("checkStatus")]
    public bool CheckStatus { get; set; } = true;

    [JsonPropertyName("showPace")]
    public bool ShowPace { get; set; } = true;

    [JsonPropertyName("showHistory")]
    public bool ShowHistory { get; set; } = true;

    [JsonPropertyName("showCost")]
    public bool ShowCost { get; set; } = true;

    /// <summary>Opt-in: renew expired Codex/Claude tokens and write them back to the CLI's credential file.</summary>
    [JsonPropertyName("refreshTokens")]
    public bool RefreshTokens { get; set; }

    /// <summary>Provider id the tray meter follows, or "auto" for the most constrained enabled provider.</summary>
    [JsonPropertyName("trayProvider")]
    public string TrayProvider { get; set; } = "auto";

    [JsonPropertyName("trayIconPerProvider")]
    public bool TrayIconPerProvider { get; set; }

    /// <summary>Outage hub: watch public status feeds of cloud and productivity platforms.</summary>
    [JsonPropertyName("outageHub")]
    public bool OutageHub { get; set; } = true;

    /// <summary>Per-source switch for the outage hub. A missing entry means on.</summary>
    [JsonPropertyName("outageSources")]
    public Dictionary<string, bool> OutageSources { get; set; } = [];

    /// <summary>Extra Statuspage sites the user added to the outage hub, as https base URLs.</summary>
    [JsonPropertyName("customStatusPages")]
    public List<string> CustomStatusPages { get; set; } = [];

    [JsonPropertyName("notifyOutages")]
    public bool NotifyOutages { get; set; } = true;

    /// <summary>Protected secrets (API keys, GitHub token) keyed by provider id. Never plain text.</summary>
    [JsonPropertyName("secrets")]
    public Dictionary<string, string> Secrets { get; set; } = [];
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string path;
    private readonly ISecretProtector protector;
    private readonly object gate = new();
    private AppConfig config;

    public ConfigStore(string path, ISecretProtector protector)
    {
        this.path = path;
        this.protector = protector;
        this.config = Load(path, out var loadError);
        this.LoadError = loadError;
    }

    /// <summary>Set when an existing config file could not be read. Such a file is never overwritten.</summary>
    public string? LoadError { get; }

    public AppConfig Current
    {
        get
        {
            lock (this.gate)
            {
                return this.config;
            }
        }
    }

    public int RefreshSeconds => Math.Max(AppConfig.MinRefreshSeconds, this.Current.RefreshSeconds);

    /// <summary>Valid thresholds in ascending order; an empty or nonsense list falls back to 90%.</summary>
    public IReadOnlyList<double> AlertThresholds
    {
        get
        {
            var valid = (this.Current.AlertThresholds ?? [])
                .Where(value => value is > 0 and <= 100)
                .Distinct()
                .Order()
                .ToList();
            return valid.Count == 0 ? [90] : valid;
        }
    }

    /// <summary>Built-in sources plus valid custom Statuspage sites, whether or not they are switched on.</summary>
    public IReadOnlyList<OutageSource> AllOutageSources()
    {
        var custom = (this.Current.CustomStatusPages ?? [])
            .Select(Core.OutageHub.CustomSource)
            .OfType<OutageSource>()
            .DistinctBy(source => source.Id);
        return [.. Core.OutageHub.DefaultSources, .. custom];
    }

    /// <summary>The sources the hub should poll right now; empty when the hub is off.</summary>
    public IReadOnlyList<OutageSource> EnabledOutageSources()
    {
        var current = this.Current;
        if (!current.OutageHub)
        {
            return [];
        }

        return this.AllOutageSources()
            .Where(source => !current.OutageSources.TryGetValue(source.Id, out var enabled) || enabled)
            .ToList();
    }

    public bool? ProviderSwitch(string providerId)
    {
        lock (this.gate)
        {
            return this.config.Providers.TryGetValue(providerId, out var enabled) ? enabled : null;
        }
    }

    public string? GetSecret(string providerId)
    {
        lock (this.gate)
        {
            return this.config.Secrets.TryGetValue(providerId, out var stored)
                ? this.protector.Unprotect(stored)
                : null;
        }
    }

    public void SetSecret(string providerId, string? value) => this.Update(config =>
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            config.Secrets.Remove(providerId);
        }
        else
        {
            config.Secrets[providerId] = this.protector.Protect(value.Trim());
        }
    });

    public void Update(Action<AppConfig> change)
    {
        lock (this.gate)
        {
            change(this.config);
            if (this.LoadError is not null)
            {
                // Keep the change for this session but leave the unreadable file alone.
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
            var temporary = this.path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this.config, Options));
            File.Move(temporary, this.path, overwrite: true);
        }
    }

    private static AppConfig Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"Could not read {path}: {failure.Message}";
            return new AppConfig();
        }
    }
}
