using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexBar.Core.Providers;

/// <summary>
/// Claude subscription usage through the OAuth API, mirroring <c>ClaudeOAuthUsageFetcher.swift</c>.
/// Reads Claude Code's <c>.credentials.json</c> and never refreshes or rewrites it: renewing the token here
/// would rotate the refresh token and sign Claude Code out.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";
    private const string UserAgent = "claude-code/2.1.0";

    public string Id => "claude";

    public string DisplayName => "Claude";

    public string DashboardUrl => "https://claude.ai/settings/usage";

    public string SetupHint => "Sign in with Claude Code. CodexBar reads ~/.claude/.credentials.json.";

    public bool UsesApiKey => false;

    public string? StatusPageUrl => "https://status.claude.com";

    public static string CredentialsPath(ProviderContext context)
    {
        var configured = context.Environment("CLAUDE_CONFIG_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(context.HomeDirectory, ".claude")
            : configured.Trim();
        return Path.Combine(root, ".credentials.json");
    }

    public bool HasCredentials(ProviderContext context) => File.Exists(CredentialsPath(context));

    public async Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var path = CredentialsPath(context);
        var now = context.Clock();
        var credentials = ReadCredentials(path);
        var renewed = false;
        if (credentials.ExpiresAt is { } expiry && expiry <= now)
        {
            // Opt-in only: the renewed tokens are written back so Claude Code stays signed in.
            if (!context.AllowTokenRefresh()
                || !await TokenRefresher.RefreshClaudeAsync(context, path, cancellationToken).ConfigureAwait(false))
            {
                throw new ProviderException(
                    ProviderErrorKind.Unauthorized,
                    "Claude Code's sign-in token has expired. Open Claude Code once so it renews it, then refresh.");
            }

            credentials = ReadCredentials(path);
            renewed = true;
        }

        var (status, body) = await this.SendAsync(context, credentials, cancellationToken).ConfigureAwait(false);
        if (status == 401 && !renewed && context.AllowTokenRefresh()
            && await TokenRefresher.RefreshClaudeAsync(context, path, cancellationToken).ConfigureAwait(false))
        {
            credentials = ReadCredentials(path);
            (status, body) = await this.SendAsync(context, credentials, cancellationToken).ConfigureAwait(false);
        }

        switch (status)
        {
            case 200:
                return ParseUsage(body, credentials, now);
            case 401:
                throw new ProviderException(
                    ProviderErrorKind.Unauthorized,
                    "Claude rejected the sign-in token. Open Claude Code to renew it, or sign in again.");
            case 403 when body.Contains("user:profile", StringComparison.Ordinal):
                throw new ProviderException(
                    ProviderErrorKind.Unauthorized,
                    "This Claude token cannot read usage (missing user:profile scope). Sign in to Claude Code again.");
            case 429:
                throw new ProviderException(
                    ProviderErrorKind.RateLimited,
                    "Claude's usage endpoint is rate limited right now. Wait a few minutes, then refresh.");
            default:
                throw new ProviderException(
                    ProviderErrorKind.Server,
                    $"Claude API error {status}: {ProviderHttp.Shorten(body)}");
        }
    }

    private static ClaudeCredentials ReadCredentials(string path)
    {
        try
        {
            return ParseCredentials(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Claude Code is not signed in on this PC. Run `claude` and sign in.",
                error);
        }
    }

    private async Task<(int Status, string Body)> SendAsync(
        ProviderContext context,
        ClaudeCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return await ProviderHttp.SendAsync(context.Http, request, this.DisplayName, cancellationToken)
            .ConfigureAwait(false);
    }

    public static ClaudeCredentials ParseCredentials(string credentialsJson)
    {
        using var document = JsonHelpers.TryParse(credentialsJson)
            ?? throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Claude Code's credentials file is not valid JSON.");
        var oauth = document.RootElement.Get("claudeAiOauth");
        var accessToken = oauth.Get("accessToken").AsString()
            ?? throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Claude Code's credentials file has no subscription sign-in. Run `claude` and sign in.");

        DateTimeOffset? expiresAt = null;
        if (oauth.Get("expiresAt").AsDouble() is { } millis && millis > 0 && millis < 253_402_300_799_000)
        {
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
        }

        return new ClaudeCredentials(
            accessToken,
            expiresAt,
            oauth.Get("subscriptionType").AsString(),
            oauth.Get("rateLimitTier").AsString());
    }

    public static UsageSnapshot ParseUsage(string body, ClaudeCredentials credentials, DateTimeOffset now)
    {
        using var document = ProviderHttp.ParseOrThrow(body, "Claude");
        var root = document.RootElement;
        var windows = new List<RateWindow>();

        // Session and weekly never substitute for each other: a missing five-hour reading stays absent.
        AddWindow(windows, root.Get("five_hour"), "claude-session", "Session (5h)", 5 * 60);
        AddWindow(windows, root.Get("seven_day") ?? root.Get("seven_day_oauth_apps"), "claude-weekly", "Weekly", 7 * 24 * 60);
        AddWindow(windows, root.Get("seven_day_sonnet"), "claude-weekly-sonnet", "Sonnet only · Weekly", 7 * 24 * 60);
        AddWindow(windows, root.Get("seven_day_opus"), "claude-weekly-opus", "Opus only · Weekly", 7 * 24 * 60);
        AddScopedWeeklyLimits(windows, root.Get("limits"));

        var details = new List<DetailRow>();
        var extra = root.Get("extra_usage");
        if (extra.Get("is_enabled").AsBool() == true
            && extra.Get("used_credits").AsDouble() is { } usedCents
            && extra.Get("monthly_limit").AsDouble() is { } limitCents)
        {
            // OAuth extra usage is reported in cents.
            var currency = extra.Get("currency").AsString() ?? "USD";
            var used = usedCents / 100;
            var limit = limitCents / 100;
            details.Add(new DetailRow(
                "Extra usage",
                $"{UsageFormatter.Money(used, currency)} / {UsageFormatter.Money(limit, currency)}",
                limit > 0 ? Math.Clamp(used / limit, 0, 1) : null));
        }

        return new UsageSnapshot("claude", windows, details, FormatPlan(credentials), null, now);
    }

    private static void AddWindow(List<RateWindow> windows, JsonElement? window, string id, string title, int minutes)
    {
        if (window.Get("utilization").AsDouble() is not { } utilization)
        {
            return;
        }

        windows.Add(new RateWindow(
            id,
            title,
            Math.Clamp(utilization, 0, 100),
            minutes,
            window.Get("resets_at").AsIsoDate()));
    }

    /// <summary>Newer response shape: model-scoped weekly limits in a flat <c>limits</c> list.</summary>
    private static void AddScopedWeeklyLimits(List<RateWindow> windows, JsonElement? limits)
    {
        foreach (var item in limits.Items())
        {
            JsonElement? entry = item;
            if (entry.Get("group").AsString() != "weekly" || entry.Get("kind").AsString() != "weekly_scoped")
            {
                continue;
            }

            var model = entry.Get("scope").Get("model");
            var name = model.Get("display_name").AsString();
            if (name is null || entry.Get("percent").AsDouble() is not { } percent)
            {
                continue;
            }

            var slug = Slug(model.Get("id").AsString() ?? name);
            if (slug.Length == 0 || slug is "all" or "all-models")
            {
                continue;
            }

            var id = $"claude-weekly-scoped-{slug}";
            var title = $"{name} only · Weekly";
            if (windows.Any(existing => existing.Id == id || existing.Title == title))
            {
                continue;
            }

            windows.Add(new RateWindow(
                id,
                title,
                Math.Clamp(percent, 0, 100),
                7 * 24 * 60,
                entry.Get("resets_at").AsIsoDate()));
        }
    }

    private static string Slug(string value)
    {
        var parts = new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('-', parts);
    }

    private static string? FormatPlan(ClaudeCredentials credentials)
    {
        var tier = credentials.RateLimitTier?.ToLowerInvariant() ?? string.Empty;
        var type = credentials.SubscriptionType?.ToLowerInvariant();
        var plan = type
            ?? (tier.Contains("max", StringComparison.Ordinal) ? "max"
                : tier.Contains("pro", StringComparison.Ordinal) ? "pro"
                : tier.Contains("team", StringComparison.Ordinal) ? "team"
                : tier.Contains("enterprise", StringComparison.Ordinal) ? "enterprise"
                : null);
        if (plan is null)
        {
            return null;
        }

        var label = UsageFormatter.Capitalize(plan);
        if (plan == "max")
        {
            if (tier.Contains("20x", StringComparison.Ordinal))
            {
                label += " 20x";
            }
            else if (tier.Contains("5x", StringComparison.Ordinal))
            {
                label += " 5x";
            }
        }

        return label;
    }
}

public sealed record ClaudeCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string? RateLimitTier);
