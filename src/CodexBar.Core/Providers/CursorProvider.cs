using System.Text;
using System.Text.Json;

namespace CodexBar.Core.Providers;

/// <summary>
/// Cursor plan usage from cursor.com, a reduced port of <c>CursorStatusProbe.swift</c>.
/// Automatic mode reuses the Cursor app's own sign-in token from its local state database (read-only, never
/// refreshed). A Cookie header pasted in Settings overrides it. Browser cookie import, team budgets and the
/// dashboard cost feed of the macOS app are not ported.
/// </summary>
public sealed class CursorProvider : IUsageProvider
{
    private const string Origin = "https://cursor.com";
    private const string TokenQuery = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1;";

    public string Id => "cursor";

    public string DisplayName => "Cursor";

    public string DashboardUrl => "https://cursor.com/dashboard?tab=usage";

    public string SetupHint =>
        "Sign in to the Cursor app; CodexBar.Net reads its local sign-in. "
        + "Or paste the Cookie header from a cursor.com request.";

    public bool UsesApiKey => true;

    public string SecretPrompt => "Optional: paste a cursor.com Cookie header";

    public string? StatusPageUrl => "https://status.cursor.com";

    public static string StateDatabasePath(ProviderContext context) =>
        Path.Combine(context.AppDataDirectory, "Cursor", "User", "globalStorage", "state.vscdb");

    public bool HasCredentials(ProviderContext context) =>
        !string.IsNullOrWhiteSpace(context.Secrets(this.Id)) || File.Exists(StateDatabasePath(context));

    public async Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var now = context.Clock();
        var session = ResolveSession(context, now);

        var summary = await SendAsync(context, HttpMethod.Get, "/api/usage-summary", session.CookieHeader, cancellationToken)
            .ConfigureAwait(false);
        switch (summary.Status)
        {
            case 200:
                break;
            case 401 or 403:
                throw new ProviderException(
                    ProviderErrorKind.Unauthorized,
                    session.FromApp
                        ? "Cursor rejected the app's sign-in. Open Cursor and sign in again."
                        : "Cursor rejected the pasted cookie. Paste a fresh Cookie header in Settings.");
            case 429:
                throw new ProviderException(ProviderErrorKind.RateLimited, "Cursor is rate limiting usage requests. Try again shortly.");
            default:
                throw new ProviderException(
                    ProviderErrorKind.Server,
                    $"Cursor API error {summary.Status}: {ProviderHttp.Shorten(summary.Body)}");
        }

        // Everything below is optional detail: a failure must leave the plan bars intact.
        var me = await this.OptionalAsync(context, HttpMethod.Get, "/api/auth/me", session.CookieHeader, cancellationToken)
            .ConfigureAwait(false);
        var grokBot = await this.OptionalAsync(
            context,
            HttpMethod.Post,
            "/api/dashboard/get-sand-usage-status",
            session.CookieHeader,
            cancellationToken).ConfigureAwait(false);
        string? requests = null;
        if ((UserId(me) ?? session.UserId) is { } userId)
        {
            requests = await this.OptionalAsync(
                context,
                HttpMethod.Get,
                "/api/usage?user=" + Uri.EscapeDataString(userId),
                session.CookieHeader,
                cancellationToken).ConfigureAwait(false);
        }

        return ParseUsage(summary.Body, me, grokBot, requests, now);
    }

    /// <summary>Builds the session cookie Cursor's web API expects from the app's JWT access token.</summary>
    public static CursorSession SessionFromAccessToken(string accessToken, DateTimeOffset now)
    {
        using var claims = JsonHelpers.TryDecodeJwtPayload(accessToken)
            ?? throw new ProviderException(ProviderErrorKind.Unauthorized, "The Cursor app's sign-in token is not readable.");
        JsonElement? root = claims.RootElement;
        var userId = root.Get("sub").AsString()?.Split('|', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(userId) || !userId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ProviderException(ProviderErrorKind.Unauthorized, "The Cursor app's sign-in token has no usable user ID.");
        }

        if (root.Get("exp").AsUnixSeconds() is not { } expiry || expiry - now <= TimeSpan.FromSeconds(60))
        {
            throw new ProviderException(
                ProviderErrorKind.Unauthorized,
                "The Cursor app's sign-in has expired. Open Cursor so it signs in again, or paste a cursor.com cookie in Settings.");
        }

        return new CursorSession($"WorkosCursorSessionToken={userId}%3A%3A{accessToken}", userId, FromApp: true);
    }

    public static UsageSnapshot ParseUsage(
        string summaryBody,
        string? meBody,
        string? grokBotBody,
        string? requestsBody,
        DateTimeOffset now)
    {
        using var document = ProviderHttp.ParseOrThrow(summaryBody, "Cursor");
        JsonElement? summary = document.RootElement;
        var individual = summary.Get("individualUsage");
        var plan = individual.Get("plan");
        var cycleStart = summary.Get("billingCycleStart").AsIsoDate();
        var cycleEnd = summary.Get("billingCycleEnd").AsIsoDate();
        int? cycleMinutes = cycleStart is { } from && cycleEnd is { } to && to > from
            ? (int)Math.Round((to - from).TotalMinutes)
            : null;

        // Cursor's percent fields are already percentages, even when below 1 (0.36 means 0.36%).
        var auto = plan.Get("autoPercentUsed").AsDouble();
        var api = plan.Get("apiPercentUsed").AsDouble();
        var total = plan.Get("totalPercentUsed").AsDouble()
            ?? (auto is { } a && api is { } b ? (a + b) / 2 : api ?? auto)
            ?? Ratio(plan)
            ?? Ratio(individual.Get("overall"))
            ?? Ratio(summary.Get("teamUsage").Get("pooled"))
            ?? 0;

        var windows = new List<RateWindow>();
        var details = new List<DetailRow>();

        using var requestsDocument = requestsBody is null ? null : JsonHelpers.TryParse(requestsBody);
        var legacy = requestsDocument?.RootElement.Get("gpt-4");
        var requestLimit = legacy.Get("maxRequestUsage").AsDouble();
        var requestsUsed = legacy.Get("numRequestsTotal").AsDouble() ?? legacy.Get("numRequests").AsDouble();
        var hasRequestQuota = requestLimit is > 0 && requestsUsed is not null;

        using var grokBotDocument = grokBotBody is null ? null : JsonHelpers.TryParse(grokBotBody);
        var grokBot = GrokBotWindow(grokBotDocument?.RootElement, now);
        var sharedWeekly = !hasRequestQuota && grokBot is { Unified: true, Window.ResetsAt: { } weeklyReset } && weeklyReset > now;

        if (hasRequestQuota)
        {
            // Legacy request-based plans count requests, not dollars.
            windows.Add(new RateWindow(
                "cursor-requests",
                "Requests · Monthly",
                Math.Clamp(requestsUsed!.Value / requestLimit!.Value * 100, 0, 100),
                cycleMinutes,
                cycleEnd,
                $"{requestsUsed:0} / {requestLimit:0} requests"));
        }
        else if (sharedWeekly)
        {
            // Unified subscriptions share one weekly pool; the monthly breakdown is stale for them.
            windows.Add(grokBot!.Window with { Id = "cursor-weekly", Title = "Weekly" });
        }
        else
        {
            windows.Add(new RateWindow("cursor-plan", "Plan usage · Monthly", Math.Clamp(total, 0, 100), cycleMinutes, cycleEnd));
            if (auto is { } autoUsed)
            {
                windows.Add(new RateWindow("cursor-auto", "Auto models", Math.Clamp(autoUsed, 0, 100), cycleMinutes, cycleEnd));
            }

            if (api is { } apiUsed)
            {
                windows.Add(new RateWindow("cursor-api", "Named models", Math.Clamp(apiUsed, 0, 100), cycleMinutes, cycleEnd));
            }

            if (grokBot is not null)
            {
                windows.Add(grokBot.Window);
            }
        }

        // On-demand spend is reported in cents. A personal cap wins; otherwise the shared team budget.
        var onDemand = individual.Get("onDemand");
        var teamOnDemand = summary.Get("teamUsage").Get("onDemand");
        var useTeam = !(onDemand.Get("limit").AsDouble() > 0) && teamOnDemand.Get("limit").AsDouble() > 0;
        var spend = useTeam ? teamOnDemand : onDemand;
        var spent = (spend.Get("used").AsDouble() ?? 0) / 100;
        var cap = spend.Get("limit").AsDouble() / 100;
        if (spent > 0 || cap > 0)
        {
            details.Add(new DetailRow(
                useTeam ? "On-demand (team)" : "On-demand",
                cap > 0
                    ? $"{UsageFormatter.Money(spent, "USD")} / {UsageFormatter.Money(cap.Value, "USD")}"
                    : UsageFormatter.Money(spent, "USD"),
                cap > 0 ? Math.Clamp(spent / cap.Value, 0, 1) : null));
        }

        using var meDocument = meBody is null ? null : JsonHelpers.TryParse(meBody);
        return new UsageSnapshot(
            "cursor",
            windows,
            details,
            PlanName(summary.Get("membershipType").AsString()),
            meDocument?.RootElement.Get("email").AsString(),
            now);
    }

    private static double? Ratio(JsonElement? usage) =>
        usage.Get("limit").AsDouble() is { } limit && limit > 0 && usage.Get("used").AsDouble() is { } used
            ? used / limit * 100
            : null;

    /// <summary>Grok Bot's weekly allowance: shown for a paid allowance or an unexpired trial.</summary>
    private static GrokBot? GrokBotWindow(JsonElement? status, DateTimeOffset now)
    {
        if (status.Get("usagePercent").AsDouble() is not { } percent)
        {
            return null;
        }

        var hasLimit = status.Get("includedLimitZero").AsBool() is { } zero ? !zero : status.Get("hasNonZeroIncludedLimit").AsBool() == true;
        var hasTrial = !hasLimit && status.Get("sandTrialExpiresAt").AsIsoDate() > now;
        if (!hasLimit && !hasTrial)
        {
            return null;
        }

        // A trial expiry is not a recurring reset.
        var resetsAt = hasTrial ? null : status.Get("nextResetTimestampUtc").AsIsoDate();
        return new GrokBot(
            new RateWindow("cursor-grok-bot", "Grok Bot", Math.Clamp(percent, 0, 100), resetsAt is null ? null : 10080, resetsAt),
            status.Get("isUnified").AsBool() == true);
    }

    private static string? PlanName(string? membership) => membership?.ToLowerInvariant() switch
    {
        null => null,
        "enterprise" => "Enterprise",
        "express" => "Start",
        "free" => "Free",
        "free_trial" => "Pro Trial",
        "hobby" => "Hobby",
        "pro" or "pro_student" => "Pro",
        "pro_plus" => "Pro+",
        "team" => "Team",
        "ultra" => "Ultra",
        _ => membership,
    };

    private static string? UserId(string? meBody)
    {
        using var document = meBody is null ? null : JsonHelpers.TryParse(meBody);
        return document?.RootElement.Get("sub").AsString()?.Split('|', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    private static CursorSession ResolveSession(ProviderContext context, DateTimeOffset now)
    {
        // A pasted cookie is the explicit override and never falls back to the app's sign-in.
        if (context.Secrets("cursor")?.Trim() is { Length: > 0 } pasted)
        {
            var header = pasted.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) ? pasted[7..].Trim() : pasted;
            if (header.Any(c => c is '\r' or '\n'))
            {
                throw new ProviderException(ProviderErrorKind.NotConfigured, "The pasted Cursor cookie must be a single line.");
            }

            return new CursorSession(header.Contains('=') ? header : $"WorkosCursorSessionToken={header}", null, FromApp: false);
        }

        var path = StateDatabasePath(context);
        if (!File.Exists(path))
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Cursor is not signed in on this PC. Sign in to the Cursor app, or paste a cursor.com cookie in Settings.");
        }

        byte[]? value;
        try
        {
            value = WinSqlite.ReadSingleValue(path, TokenQuery);
        }
        catch (InvalidOperationException error)
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                $"Could not read the Cursor app's sign-in: {error.Message} Paste a cursor.com cookie in Settings instead.",
                error);
        }

        var token = value is null ? string.Empty : WinSqlite.DecodeText(value).Trim().Trim('"');
        if (token.Length == 0)
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "The Cursor app is not signed in. Sign in to Cursor, or paste a cursor.com cookie in Settings.");
        }

        if (token.StartsWith("cursor-safe-storage:", StringComparison.Ordinal))
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "This Cursor version encrypts its sign-in, which CodexBar.Net cannot read yet. "
                + "Paste a cursor.com cookie in Settings instead.");
        }

        return SessionFromAccessToken(token, now);
    }

    private static async Task<(int Status, string Body)> SendAsync(
        ProviderContext context,
        HttpMethod method,
        string path,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Origin + path);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        if (method == HttpMethod.Post)
        {
            // Cursor's dashboard endpoints check the Origin header for CSRF.
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await ProviderHttp.SendAsync(context.Http, request, "Cursor", cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> OptionalAsync(
        ProviderContext context,
        HttpMethod method,
        string path,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        try
        {
            var (status, body) = await SendAsync(context, method, path, cookieHeader, cancellationToken).ConfigureAwait(false);
            return status == 200 ? body : null;
        }
        catch (ProviderException)
        {
            return null;
        }
    }

    private sealed record GrokBot(RateWindow Window, bool Unified);
}

public sealed record CursorSession(string CookieHeader, string? UserId, bool FromApp);
