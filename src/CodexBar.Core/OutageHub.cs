using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CodexBar.Core;

public enum OutageFeedKind
{
    /// <summary>Atlassian Statuspage <c>api/v2/status.json</c> under the page's base URL.</summary>
    Statuspage,

    /// <summary>Azure's public status RSS feed: one item per active incident, none when healthy.</summary>
    AzureRss,

    /// <summary>AWS Health's public current-events document.</summary>
    AwsCurrentEvents,

    /// <summary>Google Cloud's public incident history; an incident without an end time is active.</summary>
    GoogleCloudIncidents,

    /// <summary>Microsoft's public service-health posts (consumer Microsoft 365 services).</summary>
    MicrosoftPosts,
}

/// <summary>One service whose public status feed the outage hub watches.</summary>
public sealed record OutageSource(string Id, string Name, OutageFeedKind Kind, string FeedUrl, string PageUrl);

/// <summary>Latest health of one source. <see cref="Status"/> is null when the feed could not be read.</summary>
public sealed record OutageReading(OutageSource Source, ProviderStatus? Status)
{
    public bool IsIncident => this.Status?.IsIncident == true;
}

/// <summary>
/// Outage hub: public status feeds of cloud and productivity platforms, next to the AI providers' own status.
/// Every feed is public and unauthenticated; requests carry no account data.
/// </summary>
public static class OutageHub
{
    public static IReadOnlyList<OutageSource> DefaultSources { get; } =
    [
        new("azure", "Microsoft Azure", OutageFeedKind.AzureRss,
            "https://azure.status.microsoft/en-us/status/feed/", "https://azure.status.microsoft/en-us/status"),
        new("aws", "Amazon Web Services", OutageFeedKind.AwsCurrentEvents,
            "https://health.aws.amazon.com/public/currentevents", "https://health.aws.amazon.com/health/status"),
        new("gcp", "Google Cloud", OutageFeedKind.GoogleCloudIncidents,
            "https://status.cloud.google.com/incidents.json", "https://status.cloud.google.com"),
        new("m365", "Microsoft 365", OutageFeedKind.MicrosoftPosts,
            "https://status.cloud.microsoft/api/posts/m365Consumer", "https://status.cloud.microsoft"),
        new("cloudflare", "Cloudflare", OutageFeedKind.Statuspage,
            "https://www.cloudflarestatus.com", "https://www.cloudflarestatus.com"),
        new("github", "GitHub", OutageFeedKind.Statuspage,
            "https://www.githubstatus.com", "https://www.githubstatus.com"),
    ];

    /// <summary>
    /// Turns a user-entered Statuspage address into a source, or null when it is not an https URL.
    /// The name shown is the host without a leading "www." or "status.".
    /// </summary>
    public static OutageSource? CustomSource(string address)
    {
        var text = address.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
        {
            return null;
        }

        var name = uri.Host;
        foreach (var prefix in new[] { "www.", "status." })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
            }
        }

        var baseUrl = $"https://{uri.Host}";
        return new OutageSource("custom:" + uri.Host.ToLowerInvariant(), name, OutageFeedKind.Statuspage, baseUrl, baseUrl);
    }

    /// <summary>Reads one source. Never throws for an unreachable or changed feed: that is an unknown status.</summary>
    public static async Task<OutageReading> FetchAsync(HttpClient http, OutageSource source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var url = source.Kind == OutageFeedKind.Statuspage
                ? source.FeedUrl.TrimEnd('/') + "/api/v2/status.json"
                : source.FeedUrl;
            var bytes = await http.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false);
            return new OutageReading(source, Parse(source.Kind, bytes));
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or XmlException or InvalidOperationException
                                          || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new OutageReading(source, null);
        }
    }

    public static ProviderStatus? Parse(OutageFeedKind kind, byte[] body) => kind switch
    {
        OutageFeedKind.Statuspage => StatusFetcher.ParseStatuspage(DecodeText(body)),
        OutageFeedKind.AzureRss => ParseAzureRss(DecodeText(body)),
        OutageFeedKind.AwsCurrentEvents => ParseAwsCurrentEvents(DecodeText(body)),
        OutageFeedKind.GoogleCloudIncidents => ParseGoogleCloud(DecodeText(body)),
        OutageFeedKind.MicrosoftPosts => ParseMicrosoftPosts(DecodeText(body)),
        _ => null,
    };

    /// <summary>AWS serves its feed as UTF-16 with a byte-order mark; everything else is UTF-8.</summary>
    public static string DecodeText(byte[] body)
    {
        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2);
        }

        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(body, 2, body.Length - 2);
        }

        return Encoding.UTF8.GetString(body).TrimStart('﻿');
    }

    /// <summary>Each RSS item is an active Azure incident. The feed does not grade severity, so any item is "minor".</summary>
    public static ProviderStatus? ParseAzureRss(string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (XmlException)
        {
            return null;
        }

        var channel = document.Root?.Element("channel");
        if (channel is null)
        {
            return null;
        }

        var items = channel.Elements("item").ToList();
        if (items.Count == 0)
        {
            return new ProviderStatus(StatusIndicator.None, null, ParseDate(channel.Element("lastBuildDate")?.Value));
        }

        var title = items[0].Element("title")?.Value.Trim();
        return new ProviderStatus(
            StatusIndicator.Minor,
            Describe(title, items.Count),
            ParseDate(items[0].Element("pubDate")?.Value));
    }

    /// <summary>
    /// AWS lists only current events. Status 1 is informational, 2 degraded, 3 disrupted; the worst one is reported
    /// with its service and region.
    /// </summary>
    public static ProviderStatus? ParseAwsCurrentEvents(string body)
    {
        using var document = JsonHelpers.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var worst = StatusIndicator.None;
        string? description = null;
        DateTimeOffset? updated = null;
        var count = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            JsonElement? entry = item;
            var indicator = entry.Get("status").AsDouble() switch
            {
                >= 3 => StatusIndicator.Major,
                >= 2 => StatusIndicator.Minor,
                >= 1 => StatusIndicator.Minor,
                _ => StatusIndicator.None,
            };
            if (indicator == StatusIndicator.None)
            {
                continue;
            }

            count++;
            if (Rank(indicator) <= Rank(worst))
            {
                continue;
            }

            worst = indicator;
            var latest = entry.Get("event_log").Items().Cast<JsonElement?>().LastOrDefault();
            var summary = latest.Get("summary").AsString() ?? entry.Get("summary").AsString();
            var where = string.Join(", ", new[] { entry.Get("service_name").AsString(), entry.Get("region_name").AsString() }
                .Where(part => part is not null));
            description = string.Join(": ", new[] { where, summary }.Where(part => !string.IsNullOrEmpty(part)));
            updated = latest.Get("timestamp").AsUnixSeconds() ?? entry.Get("date").AsUnixSeconds();
        }

        return new ProviderStatus(worst, worst == StatusIndicator.None ? null : Describe(description, count), updated);
    }

    public static ProviderStatus? ParseGoogleCloud(string body)
    {
        using var document = JsonHelpers.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var worst = StatusIndicator.None;
        string? description = null;
        DateTimeOffset? updated = null;
        var count = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            JsonElement? incident = item;
            if (incident.Get("end") is not null)
            {
                continue;
            }

            count++;
            var indicator = incident.Get("status_impact").AsString()?.ToUpperInvariant() switch
            {
                "SERVICE_OUTAGE" => StatusIndicator.Critical,
                "SERVICE_DISRUPTION" => StatusIndicator.Major,
                "SERVICE_INFORMATION" => StatusIndicator.Minor,
                _ => incident.Get("severity").AsString()?.ToLowerInvariant() switch
                {
                    "high" => StatusIndicator.Critical,
                    "medium" => StatusIndicator.Major,
                    _ => StatusIndicator.Minor,
                },
            };
            if (Rank(indicator) <= Rank(worst))
            {
                continue;
            }

            worst = indicator;
            description = incident.Get("external_desc").AsString();
            updated = incident.Get("modified").AsIsoDate() ?? incident.Get("begin").AsIsoDate();
        }

        return new ProviderStatus(worst, worst == StatusIndicator.None ? null : Describe(description, count), updated);
    }

    /// <summary>
    /// One post per Microsoft 365 service. "Operational", "Available" and restored/false-positive posts are healthy;
    /// anything else is an open incident.
    /// </summary>
    public static ProviderStatus? ParseMicrosoftPosts(string body)
    {
        using var document = JsonHelpers.TryParse(body);
        if (document is null)
        {
            return null;
        }

        // The endpoint returns a list for Microsoft 365 and a single object for some other workloads.
        var posts = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : [document.RootElement];
        var worst = StatusIndicator.None;
        string? description = null;
        DateTimeOffset? updated = null;
        var count = 0;
        var sawStatus = false;
        foreach (var item in posts)
        {
            JsonElement? post = item;
            if (post.Get("Status").AsString() is not { } status)
            {
                continue;
            }

            sawStatus = true;
            var lower = status.ToLowerInvariant();
            if (lower is "operational" or "available" || lower.Contains("restored", StringComparison.Ordinal)
                || lower.Contains("false positive", StringComparison.Ordinal) || lower.Contains("resolved", StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            var indicator = lower.Contains("interruption", StringComparison.Ordinal) || lower.Contains("outage", StringComparison.Ordinal)
                ? StatusIndicator.Major
                : StatusIndicator.Minor;
            if (Rank(indicator) <= Rank(worst))
            {
                continue;
            }

            worst = indicator;
            var service = post.Get("ServiceDisplayName").AsString();
            var title = post.Get("Title").AsString() ?? status;
            description = service is null ? title : $"{service}: {title}";
            updated = post.Get("LastUpdatedTime").AsIsoDate();
        }

        if (!sawStatus)
        {
            return null;
        }

        return new ProviderStatus(worst, worst == StatusIndicator.None ? null : Describe(description, count), updated);
    }

    private static string? Describe(string? description, int count)
    {
        var text = description?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(text))
        {
            return count > 1 ? $"{count} open incidents" : null;
        }

        if (text.Length > 140)
        {
            text = text[..139].TrimEnd() + "…";
        }

        return count > 1 ? $"{text} (+{count - 1} more)" : text;
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static int Rank(StatusIndicator indicator) => indicator switch
    {
        StatusIndicator.None => 0,
        StatusIndicator.Maintenance or StatusIndicator.Unknown => 1,
        StatusIndicator.Minor => 2,
        StatusIndicator.Major => 3,
        _ => 4,
    };
}
