using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>
/// Provider service health, a reduced port of <c>ProviderStatusFetcher.swift</c>: the Statuspage
/// <c>api/v2/status.json</c> feed, and the Google Workspace incidents feed for Gemini.
/// </summary>
public static partial class StatusFetcher
{
    private const string WorkspaceIncidentsUrl = "https://www.google.com/appsstatus/dashboard/incidents.json";

    /// <summary>Returns null when the provider has no pollable feed or the feed could not be read.</summary>
    public static async Task<ProviderStatus?> FetchAsync(
        HttpClient http,
        IUsageProvider provider,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (provider.WorkspaceStatusProductId is { } productId)
            {
                var body = await http.GetStringAsync(WorkspaceIncidentsUrl, timeout.Token).ConfigureAwait(false);
                return ParseWorkspace(body, productId);
            }

            if (provider.StatusPageUrl is { } baseUrl)
            {
                var url = baseUrl.TrimEnd('/') + "/api/v2/status.json";
                var body = await http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
                return ParseStatuspage(body);
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException
                                          || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Status is a courtesy; an unreachable status page must never look like a provider outage.
        }

        return null;
    }

    public static ProviderStatus? ParseStatuspage(string body)
    {
        using var document = JsonHelpers.TryParse(body);
        if (document is null)
        {
            return null;
        }

        var status = document.RootElement.Get("status");
        if (status.Get("indicator").AsString() is not { } raw)
        {
            return null;
        }

        var indicator = raw.ToLowerInvariant() switch
        {
            "none" => StatusIndicator.None,
            "minor" => StatusIndicator.Minor,
            "major" => StatusIndicator.Major,
            "critical" => StatusIndicator.Critical,
            "maintenance" => StatusIndicator.Maintenance,
            _ => StatusIndicator.Unknown,
        };
        return new ProviderStatus(
            indicator,
            status.Get("description").AsString(),
            document.RootElement.Get("page").Get("updated_at").AsIsoDate());
    }

    /// <summary>Picks the most severe active incident affecting the product.</summary>
    public static ProviderStatus? ParseWorkspace(string body, string productId)
    {
        using var document = JsonHelpers.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ProviderStatus? best = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            JsonElement? incident = item;
            if (incident.Get("end") is not null)
            {
                continue;
            }

            var products = incident.Get("currently_affected_products") ?? incident.Get("affected_products");
            if (!products.Items().Any(product => ((JsonElement?)product).Get("id").AsString() == productId))
            {
                continue;
            }

            var update = incident.Get("most_recent_update") ?? incident.Get("updates").Items().Cast<JsonElement?>().LastOrDefault();
            var indicator = WorkspaceIndicator(
                update.Get("status").AsString() ?? incident.Get("status_impact").AsString(),
                incident.Get("severity").AsString());
            if (best is not null && Rank(indicator) <= Rank(best.Indicator))
            {
                continue;
            }

            best = new ProviderStatus(
                indicator,
                Summarize(update.Get("text").AsString() ?? incident.Get("external_desc").AsString()),
                update.Get("when").AsIsoDate() ?? incident.Get("modified").AsIsoDate() ?? incident.Get("begin").AsIsoDate());
        }

        return best ?? new ProviderStatus(StatusIndicator.None, null, null);
    }

    public static string Label(StatusIndicator indicator) => indicator switch
    {
        StatusIndicator.None => "Operational",
        StatusIndicator.Maintenance => "Maintenance",
        StatusIndicator.Minor => "Partial outage",
        StatusIndicator.Major => "Major outage",
        StatusIndicator.Critical => "Critical outage",
        _ => "Status unknown",
    };

    private static int Rank(StatusIndicator indicator) => indicator switch
    {
        StatusIndicator.None => 0,
        StatusIndicator.Maintenance or StatusIndicator.Unknown => 1,
        StatusIndicator.Minor => 2,
        StatusIndicator.Major => 3,
        _ => 4,
    };

    private static StatusIndicator WorkspaceIndicator(string? status, string? severity) =>
        status?.ToUpperInvariant() switch
        {
            "AVAILABLE" => StatusIndicator.None,
            "SERVICE_INFORMATION" => StatusIndicator.Minor,
            "SERVICE_DISRUPTION" => StatusIndicator.Major,
            "SERVICE_OUTAGE" => StatusIndicator.Critical,
            "SERVICE_MAINTENANCE" or "SCHEDULED_MAINTENANCE" => StatusIndicator.Maintenance,
            _ => severity?.ToLowerInvariant() switch
            {
                "medium" => StatusIndicator.Major,
                "high" => StatusIndicator.Critical,
                _ => StatusIndicator.Minor,
            },
        };

    /// <summary>First meaningful line of an incident update, without Markdown headings, bold or links.</summary>
    private static string? Summarize(string? text)
    {
        foreach (var raw in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var lower = line.ToLowerInvariant();
            if (line.Length == 0 || lower.StartsWith("**summary", StringComparison.Ordinal)
                || lower.StartsWith("**description", StringComparison.Ordinal) || lower == "summary")
            {
                continue;
            }

            line = MarkdownLink().Replace(line.Replace("**", string.Empty), "$1");
            line = (line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line).Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();
}
