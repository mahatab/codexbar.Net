using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexBar.Core.Providers;

/// <summary>
/// Grok (SuperGrok) credits through the Grok CLI's billing proxy, mirroring <c>GrokCreditsProxyFetcher.swift</c>.
/// Reads the token <c>grok login</c> keeps in <c>~/.grok/auth.json</c> and never refreshes it; the CLI owns renewal.
/// The <c>grok agent stdio</c> RPC, grok.com cookie and gRPC fallbacks of the macOS app are not ported.
/// </summary>
public sealed class GrokProvider : IUsageProvider
{
    private const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    private const string SettingsUrl = "https://cli-chat-proxy.grok.com/v1/settings";
    private const string OidcScopePrefix = "https://auth.x.ai::";

    public string Id => "grok";

    public string DisplayName => "Grok";

    public string DashboardUrl => "https://grok.com/";

    public string SetupHint =>
        "Sign in with the Grok CLI (`grok login`); CodexBar.Net reads ~/.grok/auth.json. "
        + "Or paste a SuperGrok bearer token, or set GROK_OAUTH_TOKEN.";

    public bool UsesApiKey => true;

    public string SecretPrompt => "Optional: paste a SuperGrok bearer token";

    public string? StatusLinkUrl => "https://status.x.ai";

    public static string AuthFilePath(ProviderContext context)
    {
        var home = context.Environment("GROK_HOME");
        var root = string.IsNullOrWhiteSpace(home) ? Path.Combine(context.HomeDirectory, ".grok") : home.Trim();
        return Path.Combine(root, "auth.json");
    }

    public bool HasCredentials(ProviderContext context) =>
        PastedToken(context) is not null || File.Exists(AuthFilePath(context));

    public async Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var now = context.Clock();
        var credentials = ResolveCredentials(context);
        if (credentials.ExpiresAt is { } expiry && expiry <= now)
        {
            throw new ProviderException(
                ProviderErrorKind.Unauthorized,
                "The Grok CLI sign-in has expired. Run `grok login`, then refresh.");
        }

        var (status, body) = await SendAsync(context, BillingUrl, credentials.AccessToken, cancellationToken).ConfigureAwait(false);
        switch (status)
        {
            case 200:
                break;
            case 401 or 403:
                throw new ProviderException(
                    ProviderErrorKind.Unauthorized,
                    "Grok rejected the sign-in token. Run `grok login`, then refresh.");
            case 429:
                throw new ProviderException(ProviderErrorKind.RateLimited, "Grok billing is rate limited. Try again shortly.");
            default:
                throw new ProviderException(ProviderErrorKind.Server, $"Grok API error {status}: {ProviderHttp.Shorten(body)}");
        }

        // The plan name lives on a separate endpoint; it is optional and must not delay or fail usage.
        string? plan = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            var settings = await SendAsync(context, SettingsUrl, credentials.AccessToken, budget.Token).ConfigureAwait(false);
            plan = settings.Status == 200 ? ParsePlan(settings.Body) : null;
        }
        catch (Exception error) when (error is ProviderException
                                          || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
        }

        return ParseUsage(body, credentials, plan, now);
    }

    /// <summary><c>auth.json</c> is a map keyed by sign-in scope; the SuperGrok OIDC entry wins over the legacy session.</summary>
    public static GrokCredentials ParseCredentials(string authJson)
    {
        using var document = JsonHelpers.TryParse(authJson)
            ?? throw new ProviderException(ProviderErrorKind.NotConfigured, "Grok auth.json is not valid JSON.");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ProviderException(ProviderErrorKind.NotConfigured, "Grok auth.json is not a JSON object.");
        }

        JsonElement? oidc = null;
        JsonElement? legacy = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // An entry without a usable token must not shadow a healthy one.
            JsonElement? entry = property.Value;
            if (entry.Get("key").AsString() is null)
            {
                continue;
            }

            if (property.Name.StartsWith(OidcScopePrefix, StringComparison.Ordinal))
            {
                oidc = entry;
            }
            else if (property.Name.Contains("/sign-in", StringComparison.Ordinal))
            {
                legacy = entry;
            }
        }

        var chosen = oidc ?? legacy
            ?? throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Grok auth.json has no sign-in token. Run `grok login`.");
        return new GrokCredentials(
            chosen.Get("key").AsString()!,
            chosen.Get("expires_at").AsIsoDate(),
            chosen.Get("email").AsString(),
            chosen.Get("auth_mode").AsString());
    }

    public static string? ParsePlan(string settingsBody)
    {
        using var document = JsonHelpers.TryParse(settingsBody);
        return PlanName(document?.RootElement.Get("subscription_tier_display").AsString());
    }

    public static UsageSnapshot ParseUsage(string body, GrokCredentials credentials, string? plan, DateTimeOffset now)
    {
        using var document = ProviderHttp.ParseOrThrow(body, "Grok");
        var config = document.RootElement.Get("config")
            ?? throw new ProviderException(ProviderErrorKind.InvalidResponse, "Grok returned no billing details.");

        var periodEnd = config.Get("currentPeriod").Get("end").AsIsoDate();
        var resetsAt = periodEnd ?? config.Get("billingPeriodEnd").AsIsoDate();
        // Match the start to the chosen end; never combine two different billing periods.
        var start = periodEnd is null
            ? config.Get("billingPeriodStart").AsIsoDate()
            : config.Get("currentPeriod").Get("start").AsIsoDate();
        int? minutes = start is { } from && resetsAt is { } to && to > from && from <= now
            ? (int)Math.Floor((to - from).TotalMinutes)
            : null;

        var reported = config.Get("creditUsagePercent").AsDouble();
        var percent = reported;
        if (percent is null && config.Get("onDemandCap").Get("val").AsDouble() is { } cap && cap > 0
            && config.Get("onDemandUsed").Get("val").AsDouble() is { } used)
        {
            percent = used / cap * 100;
        }

        var windows = new List<RateWindow>();
        if (percent is { } value)
        {
            var title = minutes is { } length && UsageFormatter.WindowTitle(length) is "Weekly" or "Monthly"
                ? UsageFormatter.WindowTitle(minutes)
                : "Credits";
            windows.Add(new RateWindow("grok-credits", title, Math.Clamp(value, 0, 100), minutes, resetsAt));
        }

        var details = new List<DetailRow>();
        if (reported is { } total)
        {
            details.AddRange(ProductShares(config.Get("productUsage"), total));
        }

        if (PrepaidBalance(config.Get("prepaidBalance")) is { } balance)
        {
            details.Add(new DetailRow("Prepaid balance", UsageFormatter.Money(balance, "USD")));
        }

        if (windows.Count == 0 && details.Count == 0)
        {
            throw new ProviderException(
                ProviderErrorKind.InvalidResponse,
                "Grok did not report a usage percentage for this billing period.");
        }

        plan ??= PlanName(config.Get("subscriptionTier").AsString() ?? document.RootElement.Get("subscriptionTier").AsString())
            ?? credentials.AuthMode?.ToLowerInvariant() switch
            {
                "oidc" => "SuperGrok",
                var other => other,
            };
        return new UsageSnapshot("grok", windows, details, plan, credentials.Email, now);
    }

    /// <summary>
    /// Per-product shares of the same credit pool. Shown only when every entry is well formed and the shares add up
    /// to the total within one point, so a partial list can never pass as complete.
    /// </summary>
    private static List<DetailRow> ProductShares(JsonElement? productUsage, double total)
    {
        var rows = new List<(string Product, double Percent)>();
        if (productUsage is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        foreach (var item in array.EnumerateArray())
        {
            JsonElement? entry = item;
            if (entry.Get("product").AsString() is not { } product || entry.Get("usagePercent").AsDouble() is not { } share || share < 0)
            {
                return [];
            }

            rows.Add((product, share));
        }

        if (rows.Count == 0 || Math.Abs(rows.Sum(row => row.Percent) - total) > 1)
        {
            return [];
        }

        return rows.Where(row => row.Percent > 0)
            .OrderByDescending(row => row.Percent)
            .Select(row => new DetailRow(ProductName(row.Product), UsageFormatter.Percent(row.Percent)))
            .ToList();
    }

    private static string ProductName(string raw) => raw switch
    {
        "GrokBuild" => "Grok Build",
        "GrokChat" => "Grok Chat",
        "GrokImagine" => "Grok Imagine",
        "GrokAppBuilder" => "Grok App Builder",
        _ => raw,
    };

    /// <summary>USD cents. An empty object is a confirmed zero; a missing or malformed wallet stays absent.</summary>
    private static double? PrepaidBalance(JsonElement? wallet)
    {
        if (wallet is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        if (!value.EnumerateObject().Any())
        {
            return 0;
        }

        return wallet.Get("val").AsDouble() is { } cents && cents >= 0 ? cents / 100 : null;
    }

    private static string? PlanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return new string(raw.ToLowerInvariant().Where(char.IsLetter).ToArray()) switch
        {
            "supergrokheavy" or "heavy" => "SuperGrok Heavy",
            "supergrok" => "SuperGrok",
            _ => raw.Trim(),
        };
    }

    private static string? PastedToken(ProviderContext context)
    {
        foreach (var candidate in new[] { context.Secrets("grok"), context.Environment("GROK_OAUTH_TOKEN") })
        {
            var token = candidate?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token[7..].Trim();
            }

            // Cookie headers and xai- management keys cannot read SuperGrok billing.
            if (!token.Contains('=') && !token.Contains(' ') && !token.StartsWith("xai-", StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
        }

        return null;
    }

    private static GrokCredentials ResolveCredentials(ProviderContext context)
    {
        var path = AuthFilePath(context);
        if (File.Exists(path))
        {
            try
            {
                return ParseCredentials(File.ReadAllText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProviderException)
            {
                if (PastedToken(context) is null)
                {
                    throw error as ProviderException
                        ?? new ProviderException(ProviderErrorKind.NotConfigured, "Could not read Grok auth.json.", error);
                }
            }
        }

        return PastedToken(context) is { } token
            ? new GrokCredentials(token, null, null, "oidc")
            : throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Grok is not signed in on this PC. Run `grok login`, or paste a SuperGrok bearer token in Settings.");
    }

    private static async Task<(int Status, string Body)> SendAsync(
        ProviderContext context,
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "CodexBar");
        return await ProviderHttp.SendAsync(context.Http, request, "Grok", cancellationToken).ConfigureAwait(false);
    }
}

public sealed record GrokCredentials(string AccessToken, DateTimeOffset? ExpiresAt, string? Email, string? AuthMode);
