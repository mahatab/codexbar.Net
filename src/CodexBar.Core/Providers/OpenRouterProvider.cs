using System.Net.Http.Headers;

namespace CodexBar.Core.Providers;

/// <summary>
/// OpenRouter credits and key limits, a reduced port of the bundled <c>openrouter.js</c> plugin:
/// account balance from <c>/credits</c> and the key's own spending limit from <c>/key</c>.
/// </summary>
public sealed class OpenRouterProvider : IUsageProvider
{
    private const string BaseUrl = "https://openrouter.ai/api/v1";

    public string Id => "openrouter";

    public string DisplayName => "OpenRouter";

    public string DashboardUrl => "https://openrouter.ai/activity";

    public string SetupHint => "Paste an OpenRouter API key, or set the OPENROUTER_API_KEY environment variable.";

    public bool UsesApiKey => true;

    public string? StatusLinkUrl => "https://status.openrouter.ai";

    public bool HasCredentials(ProviderContext context) => ResolveKey(context) is not null;

    public async Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var key = ResolveKey(context)
            ?? throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "No OpenRouter API key. Add one in Settings.");

        var credits = await this.GetAsync(context, "/credits", key, cancellationToken).ConfigureAwait(false);
        if (credits.Status is 401 or 403)
        {
            throw new ProviderException(ProviderErrorKind.Unauthorized, "OpenRouter rejected the API key.");
        }

        // The key endpoint is best-effort: provisioning keys and some key types cannot read it.
        var keyInfo = await this.GetAsync(context, "/key", key, cancellationToken).ConfigureAwait(false);
        if (credits.Status != 200 && keyInfo.Status != 200)
        {
            throw new ProviderException(
                ProviderErrorKind.Server,
                $"OpenRouter API error {credits.Status}: {ProviderHttp.Shorten(credits.Body)}");
        }

        return ParseUsage(
            credits.Status == 200 ? credits.Body : null,
            keyInfo.Status == 200 ? keyInfo.Body : null,
            context.Clock());
    }

    public static UsageSnapshot ParseUsage(string? creditsBody, string? keyBody, DateTimeOffset now)
    {
        var windows = new List<RateWindow>();
        var details = new List<DetailRow>();

        if (creditsBody is not null)
        {
            using var document = ProviderHttp.ParseOrThrow(creditsBody, "OpenRouter");
            var data = document.RootElement.Get("data");
            if (data.Get("total_credits").AsDouble() is { } total && data.Get("total_usage").AsDouble() is { } used)
            {
                details.Add(new DetailRow("Balance", UsageFormatter.Money(Math.Max(0, total - used), "USD")));
                details.Add(new DetailRow(
                    "Account usage",
                    $"{UsageFormatter.Money(used, "USD")} / {UsageFormatter.Money(total, "USD")}",
                    total > 0 ? Math.Clamp(used / total, 0, 1) : null));
            }
        }

        string? plan = null;
        if (keyBody is not null)
        {
            using var document = ProviderHttp.ParseOrThrow(keyBody, "OpenRouter");
            var data = document.RootElement.Get("data");
            if (data.Get("limit").AsDouble() is { } limit && limit > 0)
            {
                // Prefer the reported remaining amount so a freshly reset limit shows 0% used.
                var usedOfLimit = data.Get("limit_remaining").AsDouble() is { } remaining
                    ? limit - Math.Clamp(remaining, 0, limit)
                    : Math.Clamp(data.Get("usage").AsDouble() ?? 0, 0, limit);
                var reset = data.Get("limit_reset").AsString();
                windows.Add(new RateWindow(
                    "openrouter-key-limit",
                    reset is null ? "Key limit" : $"Key limit · {UsageFormatter.Capitalize(reset)}",
                    usedOfLimit / limit * 100,
                    Note: $"{UsageFormatter.Money(usedOfLimit, "USD")} of {UsageFormatter.Money(limit, "USD")}"));
            }

            if (data.Get("usage_monthly").AsDouble() is { } monthly)
            {
                details.Add(new DetailRow("This month (key)", UsageFormatter.Money(monthly, "USD")));
            }

            if (data.Get("is_free_tier").AsBool() == true)
            {
                plan = "Free tier";
            }
        }

        if (windows.Count == 0 && details.Count == 0)
        {
            throw new ProviderException(
                ProviderErrorKind.InvalidResponse,
                "OpenRouter returned no balance or limit information.");
        }

        return new UsageSnapshot("openrouter", windows, details, plan, null, now);
    }

    private static string? ResolveKey(ProviderContext context)
    {
        var stored = context.Secrets("openrouter");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored.Trim();
        }

        var fromEnvironment = context.Environment("OPENROUTER_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    private async Task<(int Status, string Body)> GetAsync(
        ProviderContext context,
        string path,
        string key,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await ProviderHttp.SendAsync(context.Http, request, this.DisplayName, cancellationToken)
            .ConfigureAwait(false);
    }
}
