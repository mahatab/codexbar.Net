namespace CodexBar.Core;

/// <summary>Everything a provider needs from its host, injectable so tests never touch real accounts.</summary>
public sealed class ProviderContext
{
    public ProviderContext(
        HttpClient http,
        string homeDirectory,
        Func<string, string?>? environment = null,
        Func<string, string?>? secrets = null,
        Func<DateTimeOffset>? clock = null,
        Func<bool>? allowTokenRefresh = null)
    {
        this.AllowTokenRefresh = allowTokenRefresh ?? (() => false);
        this.Http = http;
        this.HomeDirectory = homeDirectory;
        this.Environment = environment ?? (_ => null);
        this.Secrets = secrets ?? (_ => null);
        this.Clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public HttpClient Http { get; }

    /// <summary>The user profile directory (<c>%USERPROFILE%</c>), where CLI tools keep their dot-folders.</summary>
    public string HomeDirectory { get; }

    public Func<string, string?> Environment { get; }

    /// <summary>Looks up a secret CodexBar itself stores (API key or token) by provider id.</summary>
    public Func<string, string?> Secrets { get; }

    public Func<DateTimeOffset> Clock { get; }

    /// <summary>
    /// Whether CodexBar may renew an expired Codex or Claude token and write it back to the CLI's own file.
    /// Off unless the user opts in, because renewing rotates the token the CLI depends on.
    /// </summary>
    public Func<bool> AllowTokenRefresh { get; }

    /// <summary>The roaming app-data folder (<c>%APPDATA%</c>), where desktop apps and npm keep per-user files.</summary>
    public string AppDataDirectory
    {
        get
        {
            var configured = this.Environment("APPDATA");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(this.HomeDirectory, "AppData", "Roaming")
                : configured.Trim();
        }
    }
}

public interface IUsageProvider
{
    string Id { get; }

    string DisplayName { get; }

    string DashboardUrl { get; }

    /// <summary>How the user sets this provider up, shown in Settings.</summary>
    string SetupHint { get; }

    /// <summary>True when the provider is configured by pasting an API key into Settings.</summary>
    bool UsesApiKey { get; }

    /// <summary>What the Settings secret box asks for when <see cref="UsesApiKey"/> is true.</summary>
    string SecretPrompt => "Paste your API key";

    /// <summary>Base URL of a Statuspage-compatible status site that can be polled, if the provider has one.</summary>
    string? StatusPageUrl => null;

    /// <summary>Human status page to open. Defaults to <see cref="StatusPageUrl"/>.</summary>
    string? StatusLinkUrl => this.StatusPageUrl;

    /// <summary>Google Workspace status-dashboard product id, for providers reported there.</summary>
    string? WorkspaceStatusProductId => null;

    /// <summary>Cheap local check for credentials. Never makes a network request.</summary>
    bool HasCredentials(ProviderContext context);

    Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken);
}
