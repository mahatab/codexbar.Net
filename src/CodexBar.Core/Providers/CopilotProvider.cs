using System.Globalization;
using System.Text.Json;

namespace CodexBar.Core.Providers;

/// <summary>
/// GitHub Copilot usage through the Copilot internal usage API, mirroring <c>CopilotUsageFetcher.swift</c>.
/// The GitHub token comes from CodexBar's own device-flow sign-in (<see cref="CopilotDeviceFlow"/>).
/// </summary>
public sealed class CopilotProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.github.com/copilot_internal/user";

    public string Id => "copilot";

    public string DisplayName => "Copilot";

    public string DashboardUrl => "https://github.com/settings/copilot/features";

    public string SetupHint => "Sign in with GitHub from Settings. CodexBar.Net stores the token encrypted for your Windows account.";

    public bool UsesApiKey => false;

    public string? StatusPageUrl => "https://www.githubstatus.com";

    public bool HasCredentials(ProviderContext context) => !string.IsNullOrWhiteSpace(context.Secrets(this.Id));

    public async Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var token = context.Secrets(this.Id);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Copilot is not signed in. Open Settings and choose Sign in with GitHub.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        // The GitHub OAuth token is used directly, not a Copilot session token.
        request.Headers.TryAddWithoutValidation("Authorization", $"token {token.Trim()}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.2");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
        request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.7");
        request.Headers.TryAddWithoutValidation("X-Github-Api-Version", "2025-04-01");

        var (status, body) = await ProviderHttp.SendAsync(context.Http, request, this.DisplayName, cancellationToken)
            .ConfigureAwait(false);
        return status switch
        {
            200 => ParseUsage(body, context.Clock()),
            401 or 403 => throw new ProviderException(
                ProviderErrorKind.Unauthorized,
                "GitHub rejected the Copilot token. Sign in with GitHub again from Settings."),
            _ => throw new ProviderException(
                ProviderErrorKind.Server,
                $"Copilot API error {status}: {ProviderHttp.Shorten(body)}"),
        };
    }

    public static UsageSnapshot ParseUsage(string body, DateTimeOffset now)
    {
        using var document = ProviderHttp.ParseOrThrow(body, "Copilot");
        var root = document.RootElement;
        if (root.Get("access_type_sku").AsString() == "subscription_ended")
        {
            throw new ProviderException(ProviderErrorKind.Unauthorized, "Copilot subscription ended.");
        }

        var resetsAt = ParseResetDate(root.Get("quota_reset_date").AsString());
        var snapshots = root.Get("quota_snapshots");
        var premium = Quota.From(snapshots.Get("premium_interactions"));
        var chat = Quota.From(snapshots.Get("chat"));

        var windows = new List<RateWindow>();
        AddWindow(windows, premium, "copilot-premium", "Premium requests", resetsAt);
        AddWindow(windows, chat, "copilot-chat", "Chat", resetsAt);

        var tokenBilling = root.Get("token_based_billing").AsBool() == true;
        var unlimited = premium?.Unlimited == true || chat?.Unlimited == true;
        if (windows.Count == 0 && !tokenBilling && !unlimited)
        {
            throw new ProviderException(
                ProviderErrorKind.InvalidResponse,
                "Copilot returned no quota information for this account.");
        }

        var details = new List<DetailRow>();
        var creditsUsed = premium?.CreditsUsed ?? chat?.CreditsUsed;
        if (creditsUsed is { } credits && (tokenBilling || unlimited || credits > 0))
        {
            details.Add(new DetailRow("Credits used", credits.ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (unlimited && windows.Count == 0)
        {
            details.Add(new DetailRow("Quota", "Unlimited"));
        }

        var plan = root.Get("copilot_plan").AsString();
        return new UsageSnapshot(
            "copilot",
            windows,
            details,
            plan is null ? null : UsageFormatter.Capitalize(plan),
            null,
            now);
    }

    private static void AddWindow(
        List<RateWindow> windows,
        Quota? quota,
        string id,
        string title,
        DateTimeOffset? resetsAt)
    {
        if (quota is not { Unlimited: false, PercentRemaining: { } remaining } || quota.IsPlaceholder)
        {
            return;
        }

        var used = Math.Max(0, 100 - remaining);
        windows.Add(new RateWindow(
            id,
            title,
            Math.Min(used, 100),
            null,
            resetsAt,
            used > 100 ? $"{used:0}% used (over quota)" : null));
    }

    private static DateTimeOffset? ParseResetDate(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record Quota(double? Entitlement, double? Remaining, double? PercentRemaining, double? CreditsUsed, bool Unlimited)
    {
        /// <summary>
        /// GitHub returns zero-entitlement, zero-remaining snapshots for token-billed seats, sometimes with
        /// percent_remaining=100; showing those would render a misleading "0% used" bar.
        /// </summary>
        public bool IsPlaceholder => !this.Unlimited && this.Entitlement == 0 && this.Remaining == 0;

        public static Quota? From(JsonElement? element)
        {
            if (element is not { ValueKind: JsonValueKind.Object })
            {
                return null;
            }

            var entitlement = element.Get("entitlement").AsDouble();
            var remaining = element.Get("remaining").AsDouble();
            var percent = element.Get("percent_remaining").AsDouble();
            if (percent is null && entitlement is > 0 && remaining is { } left)
            {
                percent = left / entitlement.Value * 100;
            }

            return new Quota(
                entitlement,
                remaining,
                percent,
                element.Get("credits_used").AsDouble(),
                element.Get("unlimited").AsBool() == true);
        }
    }
}
