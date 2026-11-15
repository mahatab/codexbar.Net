using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexBar.Core.Providers;

/// <summary>
/// Codex usage through the OAuth API, mirroring <c>CodexOAuthUsageFetcher.swift</c>.
/// Reads the Codex CLI's own <c>auth.json</c> and never writes to it; the CLI owns token renewal.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    public string Id => "codex";

    public string DisplayName => "Codex";

    public string DashboardUrl => "https://chatgpt.com/codex/settings/usage";

    public string SetupHint => "Sign in with the Codex CLI (`codex login`). CodexBar.Net reads ~/.codex/auth.json.";

    public bool UsesApiKey => false;

    public string? StatusPageUrl => "https://status.openai.com";

    public static string AuthFilePath(ProviderContext context)
    {
        var home = context.Environment("CODEX_HOME");
        var root = string.IsNullOrWhiteSpace(home) ? Path.Combine(context.HomeDirectory, ".codex") : home.Trim();
        return Path.Combine(root, "auth.json");
    }

    public bool HasCredentials(ProviderContext context) => File.Exists(AuthFilePath(context));

    public async Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var path = AuthFilePath(context);
        var credentials = ReadCredentials(path);
        var (status, body) = await this.SendAsync(context, credentials, cancellationToken).ConfigureAwait(false);
        if (status is 401 or 403 && context.AllowTokenRefresh()
            && await TokenRefresher.RefreshCodexAsync(context, path, cancellationToken).ConfigureAwait(false))
        {
            // Opt-in only: the renewed tokens were written back to auth.json so the Codex CLI stays signed in.
            credentials = ReadCredentials(path);
            (status, body) = await this.SendAsync(context, credentials, cancellationToken).ConfigureAwait(false);
        }

        switch (status)
        {
            case 200:
                break;
            case 401 or 403:
                throw new ProviderException(
                    ProviderErrorKind.Unauthorized,
                    "Codex sign-in expired or was rejected. Run `codex login`, or use Codex once so it renews its token.");
            case 429:
                throw new ProviderException(ProviderErrorKind.RateLimited, "Codex usage API is rate limited. Try again shortly.");
            default:
                throw new ProviderException(
                    ProviderErrorKind.Server,
                    $"Codex API error {status}: {ProviderHttp.Shorten(body)}");
        }

        return ParseUsage(body, credentials, context.Clock());
    }

    private async Task<(int Status, string Body)> SendAsync(
        ProviderContext context,
        CodexCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("User-Agent", "CodexBar");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (credentials.AccountId is not null)
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        return await ProviderHttp.SendAsync(context.Http, request, this.DisplayName, cancellationToken)
            .ConfigureAwait(false);
    }

    public static CodexCredentials ReadCredentials(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Codex is not signed in on this PC. Run `codex login`.",
                error);
        }

        return ParseCredentials(text);
    }

    public static CodexCredentials ParseCredentials(string authJson)
    {
        using var document = JsonHelpers.TryParse(authJson)
            ?? throw new ProviderException(ProviderErrorKind.NotConfigured, "Codex auth.json is not valid JSON.");
        var tokens = document.RootElement.Get("tokens");
        var accessToken = tokens.Get("access_token", "accessToken").AsString();
        if (accessToken is null)
        {
            // API-key logins have no ChatGPT plan quota to report.
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Codex auth.json has no ChatGPT sign-in tokens. Run `codex login` and choose ChatGPT sign-in.");
        }

        var idToken = tokens.Get("id_token", "idToken").AsString();
        string? email = null;
        string? accountId = tokens.Get("account_id", "accountId").AsString();
        using (var claims = JsonHelpers.TryDecodeJwtPayload(idToken))
        {
            if (claims is not null)
            {
                email = claims.RootElement.Get("email").AsString();
                accountId ??= claims.RootElement.Get("https://api.openai.com/auth").Get("chatgpt_account_id").AsString();
            }
        }

        return new CodexCredentials(accessToken, accountId, email);
    }

    public static UsageSnapshot ParseUsage(string body, CodexCredentials credentials, DateTimeOffset now)
    {
        using var document = ProviderHttp.ParseOrThrow(body, "Codex");
        var root = document.RootElement;
        var windows = new List<RateWindow>();
        var rateLimit = root.Get("rate_limit");
        AddWindow(windows, rateLimit.Get("primary_window"), "codex-primary", null);
        AddWindow(windows, rateLimit.Get("secondary_window"), "codex-secondary", null);

        var index = 0;
        foreach (var extra in root.Get("additional_rate_limits").Items())
        {
            index++;
            var name = ((JsonElement?)extra).Get("limit_name").AsString()
                ?? ((JsonElement?)extra).Get("metered_feature").AsString()
                ?? $"Extra limit {index}";
            var extraLimit = ((JsonElement?)extra).Get("rate_limit");
            AddWindow(windows, extraLimit.Get("primary_window"), $"codex-extra-{index}-primary", name);
            AddWindow(windows, extraLimit.Get("secondary_window"), $"codex-extra-{index}-secondary", name);
        }

        var details = new List<DetailRow>();
        var credits = root.Get("credits");
        if (credits.Get("unlimited").AsBool() == true)
        {
            details.Add(new DetailRow("Credits", "Unlimited"));
        }
        else if (credits.Get("balance").AsDouble() is { } balance
                 && (credits.Get("has_credits").AsBool() == true || balance > 0))
        {
            details.Add(new DetailRow("Credits", balance.ToString("N0", CultureInfo.CurrentCulture)));
        }

        return new UsageSnapshot(
            "codex",
            windows,
            details,
            FormatPlan(root.Get("plan_type").AsString()),
            credentials.Email,
            now);
    }

    private static void AddWindow(List<RateWindow> windows, JsonElement? window, string id, string? prefix)
    {
        if (window.Get("used_percent").AsDouble() is not { } used)
        {
            return;
        }

        var seconds = window.Get("limit_window_seconds").AsDouble();
        var minutes = seconds is { } value && value > 0 ? (int)Math.Round(value / 60) : (int?)null;
        var lane = UsageFormatter.WindowTitle(minutes);
        windows.Add(new RateWindow(
            id,
            prefix is null ? lane : $"{prefix} · {lane}",
            Math.Clamp(used, 0, 100),
            minutes,
            window.Get("reset_at").AsUnixSeconds()));
    }

    private static string? FormatPlan(string? raw) => raw switch
    {
        null => null,
        "free_workspace" => "Free Workspace",
        "k12" => "K12",
        "edu" => "Edu",
        _ => string.Join(' ', raw.Split('_').Select(UsageFormatter.Capitalize)),
    };
}

public sealed record CodexCredentials(string AccessToken, string? AccountId, string? Email);
