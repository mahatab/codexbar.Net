using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexBar.Core;
using CodexBar.Core.Providers;

namespace CodexBar.Core.Tests;

public sealed class FeatureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "codexbar-tests-" + Guid.NewGuid().ToString("N"));

    public FeatureTests() => Directory.CreateDirectory(this.directory);

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    private static string Jwt(string payloadJson) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    private ProviderContext NewContext(
        Func<HttpRequestMessage, HttpResponseMessage>? reply = null,
        Func<string, string?>? secrets = null,
        Func<string, string?>? environment = null,
        bool allowTokenRefresh = false) =>
        new(
            new HttpClient(new StubHandler(reply ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
            this.directory,
            environment,
            secrets,
            () => Now,
            () => allowTokenRefresh);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(this.directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ----- Status -----

    [Fact]
    public void Statuspage_feed_maps_indicator_and_description()
    {
        var status = StatusFetcher.ParseStatuspage(
            """{"page":{"updated_at":"2026-11-13T10:00:00Z"},"status":{"indicator":"major","description":"Partial System Outage"}}""");

        Assert.Equal(StatusIndicator.Major, status!.Indicator);
        Assert.Equal("Partial System Outage", status.Description);
        Assert.True(status.IsIncident);
        Assert.False(StatusFetcher.ParseStatuspage("""{"status":{"indicator":"none"}}""")!.IsIncident);
        Assert.Null(StatusFetcher.ParseStatuspage("<html>maintenance</html>"));
    }

    [Fact]
    public void Workspace_feed_picks_most_severe_active_incident_for_the_product()
    {
        const string feed = """
            [
              {"begin":"2026-11-13T08:00:00Z","end":"2026-11-13T09:00:00Z","severity":"high",
               "affected_products":[{"id":"gem"}],"most_recent_update":{"status":"SERVICE_OUTAGE","text":"Resolved"}},
              {"begin":"2026-11-13T10:00:00Z","severity":"low",
               "affected_products":[{"id":"other"}],"most_recent_update":{"status":"SERVICE_OUTAGE","text":"Not ours"}},
              {"begin":"2026-11-13T10:30:00Z","severity":"low",
               "currently_affected_products":[{"id":"gem"}],
               "most_recent_update":{"status":"SERVICE_INFORMATION","text":"**Summary**\nMinor issue"}},
              {"begin":"2026-11-13T11:00:00Z","severity":"medium",
               "affected_products":[{"id":"gem"}],
               "most_recent_update":{"when":"2026-11-13T11:05:00Z","status":"SERVICE_DISRUPTION","text":"**Summary:**\n- Users see [errors](https://example.com) in Gemini"}}
            ]
            """;

        var status = StatusFetcher.ParseWorkspace(feed, "gem");

        Assert.Equal(StatusIndicator.Major, status!.Indicator);
        Assert.Equal("Users see errors in Gemini", status.Description);
        Assert.Equal(StatusIndicator.None, StatusFetcher.ParseWorkspace(feed, "unaffected")!.Indicator);
    }

    [Fact]
    public async Task Store_attaches_status_and_keeps_it_when_usage_fails()
    {
        var provider = new FakeProvider { StatusPageUrl = "https://status.example.com" };
        var context = this.NewContext(request => request.RequestUri!.Host == "status.example.com"
            ? Json("""{"status":{"indicator":"minor","description":"Degraded"}}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var store = new UsageStore(context, this.NewConfig(), [provider]);

        await store.RefreshAllAsync(CancellationToken.None);
        provider.Next = () => throw new ProviderException(ProviderErrorKind.Network, "offline");
        await store.RefreshAllAsync(CancellationToken.None);

        var state = store.State("fake");
        Assert.Equal(StatusIndicator.Minor, state.Status!.Indicator);
        Assert.Equal("offline", state.Error);
        Assert.NotNull(state.Snapshot);
    }

    // ----- Outage hub -----

    [Fact]
    public void Azure_feed_is_healthy_without_items_and_reports_the_first_incident()
    {
        const string healthy = """<?xml version="1.0" encoding="utf-8"?><rss version="2.0"><channel><title>Azure Status</title><lastBuildDate>Sat, 14 Nov 2026 23:10:00 Z</lastBuildDate></channel></rss>""";
        const string incident = """
            <rss version="2.0"><channel><title>Azure Status</title>
              <item><title>Azure Front Door - Connectivity issues</title><pubDate>Fri, 13 Nov 2026 11:00:00 Z</pubDate></item>
              <item><title>Second incident</title></item>
            </channel></rss>
            """;

        Assert.Equal(StatusIndicator.None, OutageHub.ParseAzureRss(healthy)!.Indicator);
        var status = OutageHub.ParseAzureRss(incident)!;
        Assert.Equal(StatusIndicator.Minor, status.Indicator);
        Assert.Equal("Azure Front Door - Connectivity issues (+1 more)", status.Description);
        Assert.Null(OutageHub.ParseAzureRss("<html>not a feed"));
    }

    [Fact]
    public void Aws_feed_is_decoded_from_utf16_and_reports_the_worst_event()
    {
        const string json = """
            [{"date":"1772369485","status":"1","service_name":"Amazon S3","region_name":"Ohio","summary":"Informational"},
             {"date":"1772369485","status":"3","service_name":"Amazon EC2","region_name":"N. Virginia","summary":"Region Availability",
              "event_log":[{"summary":"Investigating","timestamp":1772369485},{"summary":"Increased Error Rates","timestamp":1789468030}]}]
            """;
        byte[] body = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(json)];

        var status = OutageHub.Parse(OutageFeedKind.AwsCurrentEvents, body)!;

        Assert.Equal(StatusIndicator.Major, status.Indicator);
        Assert.Equal("Amazon EC2, N. Virginia: Increased Error Rates (+1 more)", status.Description);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789468030), status.UpdatedAt);
        Assert.Equal(StatusIndicator.None, OutageHub.ParseAwsCurrentEvents("[]")!.Indicator);
    }

    [Fact]
    public void Google_cloud_feed_ignores_ended_incidents()
    {
        const string feed = """
            [{"begin":"2026-11-10T00:00:00+00:00","end":"2026-11-10T04:00:00+00:00","severity":"high","external_desc":"Old outage"},
             {"begin":"2026-11-13T10:00:00+00:00","severity":"medium","status_impact":"SERVICE_DISRUPTION",
              "external_desc":"Cloud Run is experiencing elevated latency","modified":"2026-11-13T11:00:00+00:00"}]
            """;

        var status = OutageHub.ParseGoogleCloud(feed)!;

        Assert.Equal(StatusIndicator.Major, status.Indicator);
        Assert.Equal("Cloud Run is experiencing elevated latency", status.Description);
        Assert.Equal(StatusIndicator.None, OutageHub.ParseGoogleCloud("""[{"begin":"x","end":"y"}]""")!.Indicator);
    }

    [Fact]
    public void Microsoft_posts_treat_operational_and_restored_as_healthy()
    {
        const string healthy = """
            [{"ServiceDisplayName":"Microsoft 365 (Consumer)","Title":"Earlier problem","Status":"Service restored"},
             {"ServiceDisplayName":"Outlook.com","Title":null,"Status":"Operational"}]
            """;
        const string degraded = """
            [{"ServiceDisplayName":"OneDrive","Title":null,"Status":"Operational"},
             {"ServiceDisplayName":"Outlook.com","Title":"Users may be unable to send email","Status":"Service degradation","LastUpdatedTime":"2026-11-13T10:00:00+00:00"},
             {"ServiceDisplayName":"Teams","Title":"Users cannot join meetings","Status":"Service interruption"}]
            """;

        Assert.Equal(StatusIndicator.None, OutageHub.ParseMicrosoftPosts(healthy)!.Indicator);
        var status = OutageHub.ParseMicrosoftPosts(degraded)!;
        Assert.Equal(StatusIndicator.Major, status.Indicator);
        Assert.Equal("Teams: Users cannot join meetings (+1 more)", status.Description);
        Assert.Equal(StatusIndicator.None, OutageHub.ParseMicrosoftPosts("""{"Message":"No Azure issues detected","Status":"Available"}""")!.Indicator);
        Assert.Null(OutageHub.ParseMicrosoftPosts("""[{"unexpected":true}]"""));
    }

    [Fact]
    public void Custom_status_pages_accept_only_https_hosts()
    {
        var source = OutageHub.CustomSource(" status.example.com/some/path ")!;

        Assert.Equal(("custom:status.example.com", "example.com", "https://status.example.com"), (source.Id, source.Name, source.FeedUrl));
        Assert.Equal(OutageFeedKind.Statuspage, source.Kind);
        Assert.Null(OutageHub.CustomSource("http://status.example.com"));
        Assert.Null(OutageHub.CustomSource("   "));
    }

    [Fact]
    public async Task Outage_hub_alerts_on_a_new_incident_and_keeps_the_last_status_when_a_feed_fails()
    {
        var mode = "healthy";
        var context = this.NewContext(request =>
        {
            if (request.RequestUri!.Host != "www.cloudflarestatus.com")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return mode switch
            {
                "healthy" => Json("""{"status":{"indicator":"none","description":"All Systems Operational"}}"""),
                "incident" => Json("""{"status":{"indicator":"major","description":"Partial System Outage"}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            };
        });
        var config = this.NewConfig();
        config.Update(settings =>
        {
            foreach (var source in OutageHub.DefaultSources)
            {
                settings.OutageSources[source.Id] = source.Id == "cloudflare";
            }
        });
        var clock = Now;
        var timedContext = new ProviderContext(context.Http, this.directory, clock: () => clock);
        var store = new UsageStore(timedContext, config, [new FakeProvider()]);
        var alerts = new List<UsageAlert>();
        store.Alert += alerts.Add;

        async Task RefreshAs(string next)
        {
            mode = next;
            clock = clock.AddMinutes(10);
            await store.RefreshAllAsync(CancellationToken.None);
        }

        await RefreshAs("incident");
        Assert.Empty(alerts);
        await RefreshAs("healthy");
        await RefreshAs("incident");
        await RefreshAs("incident");
        await RefreshAs("down");

        var alert = Assert.Single(alerts);
        Assert.Equal((AlertKind.Outage, "Cloudflare", "Partial System Outage"), (alert.Kind, alert.ProviderName, alert.Detail));
        var reading = Assert.Single(store.Outages);
        Assert.Equal(StatusIndicator.Major, reading.Status!.Indicator);

        config.Update(settings => settings.OutageHub = false);
        await store.RefreshAllAsync(CancellationToken.None);
        Assert.Empty(store.Outages);
    }

    // ----- Pace -----

    [Fact]
    public void Pace_reports_under_pace_run_out_and_stays_quiet_early()
    {
        // A 5-hour window with 1 hour left is 80% elapsed.
        var under = new RateWindow("w", "Session", 40, 300, Now.AddHours(1));
        Assert.Equal("40% under pace", Pace.Describe(under, Now));

        // 90% used after 2.5 of 5 hours: the last 10% lasts 2.5h * 10/90 = 16m 40s.
        var hot = new RateWindow("w", "Session", 90, 300, Now.AddMinutes(150));
        Assert.Equal("On pace to run out in 16m", Pace.Describe(hot, Now));
        Assert.Equal(180, Pace.Compute(hot, Now)!.ProjectedPercent, 3);

        Assert.Equal("Limit reached", Pace.Describe(new RateWindow("w", "Session", 100, 300, Now.AddHours(1)), Now));
        Assert.Null(Pace.Describe(new RateWindow("w", "Session", 3, 300, Now.AddMinutes(295)), Now));
        Assert.Null(Pace.Describe(new RateWindow("w", "Key limit", 50), Now));
    }

    // ----- History -----

    [Fact]
    public void History_spaces_samples_prunes_old_ones_and_survives_reload()
    {
        var path = Path.Combine(this.directory, "history.json");
        var history = new HistoryStore(path);
        UsageSnapshot At(double percent, DateTimeOffset time) =>
            new("fake", [new RateWindow("w", "Weekly", percent)], [], null, null, time);

        history.Record(At(10, Now.AddDays(-8)));
        history.Record(At(20, Now.AddMinutes(-30)));
        history.Record(At(30, Now.AddMinutes(-20)));
        history.Record(At(35, Now.AddMinutes(-19)));
        history.Record(At(40, Now));

        var samples = new HistoryStore(path).Samples("fake", "w");

        // The 8-day-old reading is pruned; the reading one minute after another replaces it.
        Assert.Equal([20, 35, 40], samples.Select(sample => sample.UsedPercent));
    }

    // ----- Cost scan -----

    [Fact]
    public void Cost_scan_prices_claude_logs_and_counts_a_repeated_message_once()
    {
        string Line(string id, string request, int output, string model, string time) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = time,
            requestId = request,
            message = new { id, model, usage = new { input_tokens = 1000, output_tokens = output, cache_read_input_tokens = 2000, cache_creation_input_tokens = 0 } },
        });
        this.Write(
            ".claude/projects/demo/session.jsonl",
            string.Join('\n',
            [
                """{"type":"user","timestamp":"2026-11-13T09:00:00Z"}""",
                Line("m1", "r1", 10, "claude-sonnet-4-5-20250929", "2026-11-13T09:00:01Z"),
                Line("m1", "r1", 500, "claude-sonnet-4-5-20250929", "2026-11-13T09:00:02Z"),
                Line("m2", "r2", 100, "claude-sonnet-4-5", "2026-11-01T09:00:00Z"),
                Line("m3", "r3", 100, "fictional-model-9", "2026-11-13T10:00:00Z"),
                Line("m4", "r4", 100, "claude-sonnet-4-5", "2026-09-01T09:00:00Z"),
                "not json",
            ]));

        var summary = new CostScanner().ScanClaude(Path.Combine(this.directory, ".claude"), Now, TimeZoneInfo.Utc);

        // Today: m1 (final streaming chunk, 3500 tokens) and the unpriced m3 (3100 tokens).
        Assert.Equal(3500 + 3100, summary.TodayTokens);
        Assert.Equal(((1000 * 3.0) + (2000 * 0.3) + (500 * 15.0)) / 1_000_000, summary.TodayCost, 9);
        Assert.Equal(3500 + 3100 + 3100, summary.MonthTokens);
        Assert.True(summary.HasUnpricedModels);
    }

    [Fact]
    public void Cost_scan_reads_codex_cumulative_totals_as_deltas()
    {
        string Tokens(int input, int cached, int output, string time) => JsonSerializer.Serialize(new
        {
            timestamp = time,
            type = "event_msg",
            payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output } } },
        });
        this.Write(
            ".codex/sessions/2026/11/13/rollout.jsonl",
            string.Join('\n',
            [
                """{"timestamp":"2026-11-13T08:00:00Z","type":"turn_context","payload":{"model":"gpt-5.1-codex-mini"}}""",
                Tokens(1000, 400, 100, "2026-11-13T08:00:01Z"),
                Tokens(1000, 400, 100, "2026-11-13T08:00:02Z"),
                Tokens(3000, 1400, 300, "2026-11-13T08:00:03Z"),
            ]));

        var summary = new CostScanner().ScanCodex(Path.Combine(this.directory, ".codex"), Now, TimeZoneInfo.Utc);

        // The repeated event adds nothing; totals end at 3000 in (1400 cached) and 300 out.
        Assert.Equal(3300, summary.TodayTokens);
        Assert.Equal(((1600 * 0.25) + (1400 * 0.025) + (300 * 2.0)) / 1_000_000, summary.TodayCost, 9);
        Assert.False(summary.HasUnpricedModels);
    }

    [Fact]
    public void Price_uses_longest_model_prefix_and_leaves_unknown_models_unpriced()
    {
        Assert.Equal(2.0, CostScanner.Price("gpt-5.1-codex-mini", 0, 0, 0, 1_000_000));
        Assert.Equal(10.0, CostScanner.Price("openai/gpt-5.1-codex", 0, 0, 0, 1_000_000));
        Assert.Equal(75.0, CostScanner.Price("claude-opus-4-20250514", 0, 0, 0, 1_000_000));
        Assert.Null(CostScanner.Price("claude-opus-4-99", 0, 0, 0, 1_000_000));
        Assert.Null(CostScanner.Price("fictional-model-9", 1, 1, 1, 1));
    }

    [Fact]
    public async Task Downloaded_price_catalog_prices_models_the_bundled_table_does_not_know()
    {
        const string catalogBody = """
            {"anthropic":{"models":{"fictional-model-9":{"cost":{"input":4,"output":20,"cache_read":0.2,"cache_write":5}},
                                    "no-cost-model":{}}},
             "openai":{"models":{"fictional-gpt":{"cost":{"input":1,"output":2}}}},
             "other":{"models":{"ignored":{"cost":{"input":9,"output":9}}}}}
            """;
        var cachePath = Path.Combine(this.directory, "pricing.json");
        var downloads = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            downloads++;
            return Json(catalogBody);
        }));
        var catalog = new PriceCatalog(cachePath);
        await catalog.EnsureFreshAsync(http, Now, CancellationToken.None);
        await catalog.EnsureFreshAsync(http, Now.AddHours(2), CancellationToken.None);

        Assert.Equal(1, downloads);
        Assert.Equal(new ModelRate(4, 0.2, 5, 20), catalog.Find("fictional-model-9")!.Standard);
        Assert.Equal(new ModelRate(1, 1, 1, 2), catalog.Find("fictional-gpt")!.Standard);
        Assert.Null(catalog.Find("ignored"));
        Assert.Equal(new ModelRate(4, 0.2, 5, 20), new PriceCatalog(cachePath).Find("FICTIONAL-MODEL-9")!.Standard);

        this.Write(
            ".claude/projects/demo/session.jsonl",
            """{"type":"assistant","timestamp":"2026-11-13T09:00:00Z","requestId":"r","message":{"id":"m","model":"fictional-model-9","usage":{"input_tokens":1000000,"output_tokens":1000000}}}""");
        var summary = new CostScanner(catalog).ScanClaude(Path.Combine(this.directory, ".claude"), Now, TimeZoneInfo.Utc);

        Assert.Equal(24, summary.TodayCost, 6);
        Assert.False(summary.HasUnpricedModels);
    }

    [Fact]
    public void Long_context_rates_apply_to_the_whole_request_only_above_the_threshold()
    {
        // GPT-5.5: $5 in / $0.50 cached / $30 out; above 272K prompt tokens 2x input and 1.5x output.
        var price = CostScanner.BundledPrice("gpt-5.5")!;
        Assert.Equal(272_000, price.LongContextThreshold);
        Assert.False(price.IsLongContext(172_000, 100_000, 0));
        Assert.True(price.IsLongContext(172_001, 100_000, 0));
        Assert.Equal(((172_000 * 5.0) + (100_000 * 0.5) + (1000 * 30.0)) / 1_000_000, price.Cost(172_000, 100_000, 0, 1000), 9);
        Assert.Equal(((172_001 * 10.0) + (100_000 * 1.0) + (1000 * 45.0)) / 1_000_000, price.Cost(172_001, 100_000, 0, 1000), 9);

        // Claude Sonnet 4.5: cache writes count toward the 200K prompt length.
        var sonnet = CostScanner.BundledPrice("claude-sonnet-4-5-20250929")!;
        Assert.Equal(((100_000 * 6.0) + (50_000 * 0.6) + (60_000 * 7.5) + (10 * 22.5)) / 1_000_000, sonnet.Cost(100_000, 50_000, 60_000, 10), 9);
        Assert.Null(CostScanner.BundledPrice("claude-sonnet-4-6")!.LongContext);
    }

    [Fact]
    public void Price_catalog_reads_long_context_tiers_and_keeps_them_across_a_reload()
    {
        const string body = """
            {"openai":{"models":{
               "tiered":{"cost":{"input":4,"output":20,"cache_read":0.4,"cache_write":5,
                 "tiers":[{"input":8,"output":30,"cache_read":0.8,"cache_write":10,"tier":{"type":"context","size":272000}}]}},
               "legacy":{"cost":{"input":3,"output":15,"cache_read":0.3,"context_over_200k":{"input":6,"output":22.5}}},
               "flat":{"cost":{"input":1,"output":2}}}}}
            """;

        var parsed = PriceCatalog.Parse(body);

        Assert.Equal(new ModelPrice(new ModelRate(4, 0.4, 5, 20), 272_000, new ModelRate(8, 0.8, 10, 30)), parsed["tiered"]);
        // The older field gives no cache rates above the threshold; they scale with the input rate.
        Assert.Equal(new ModelPrice(new ModelRate(3, 0.3, 3, 15), 200_000, new ModelRate(6, 0.6, 6, 22.5)), parsed["legacy"]);
        Assert.Null(parsed["flat"].LongContext);
    }

    [Fact]
    public void Cost_report_breaks_usage_down_by_day_model_and_project()
    {
        string Claude(string id, string model, string cwd, int input, string time) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = time,
            requestId = id,
            cwd,
            message = new { id, model, usage = new { input_tokens = input, output_tokens = 0 } },
        });
        this.Write(
            ".claude/projects/a/one.jsonl",
            string.Join('\n',
            [
                Claude("1", "claude-sonnet-4-5", @"C:\dev\projects\alpha", 1_000_000, "2026-11-13T09:00:00Z"),
                Claude("2", "claude-sonnet-4-5", @"C:\dev\projects\alpha\", 250_000, "2026-11-12T09:00:00Z"),
                Claude("3", "claude-haiku-4-5-20251001", "/home/dev/beta", 1_000_000, "2026-11-12T10:00:00Z"),
                Claude("4", "fictional-model-9", @"C:\dev\projects\alpha", 500, "2026-11-12T11:00:00Z"),
            ]));

        var summary = new CostScanner().ScanClaude(Path.Combine(this.directory, ".claude"), Now, TimeZoneInfo.Utc);
        var report = summary.Report!;

        Assert.Equal(CostScanner.WindowDays, report.Days.Count);
        Assert.Equal(new DateOnly(2026, 11, 13), report.Days[^1].Day);
        // 1M input on a long-context request costs $6; the 250K request the day before costs $1.50, Haiku $1.
        Assert.Equal(6.0, report.Days[^1].Cost, 9);
        Assert.Equal(2.5, report.Days[^2].Cost, 9);
        Assert.Equal(0, report.Days[0].Tokens);
        Assert.Equal(["claude-sonnet-4-5", "claude-haiku-4-5", "fictional-model-9"], report.Models.Select(row => row.Name));
        Assert.Equal([7.5, 1.0, 0.0], report.Models.Select(row => row.Cost));
        Assert.True(report.Models[2].HasUnpricedModels);
        Assert.Equal(["alpha", "beta"], report.Projects.Select(row => row.Name));
        Assert.Equal((7.5, 3), (report.Projects[0].Cost, report.Projects[0].Requests));
        Assert.Equal((4, 2, 2_250_500L), (report.Requests, report.LongContextRequests, report.InputTokens));
    }

    [Fact]
    public void Codex_usage_is_filed_under_the_session_working_directory()
    {
        this.Write(
            ".codex/sessions/2026/11/13/rollout.jsonl",
            string.Join('\n',
            [
                """{"timestamp":"2026-11-13T08:00:00Z","type":"session_meta","payload":{"cwd":"C:\\dev\\projects\\gamma"}}""",
                """{"timestamp":"2026-11-13T08:00:00Z","type":"turn_context","payload":{"model":"gpt-5.5"}}""",
                """{"timestamp":"2026-11-13T08:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":300000,"cached_input_tokens":0,"output_tokens":1000}}}}""",
            ]));

        var report = new CostScanner().ScanCodex(Path.Combine(this.directory, ".codex"), Now, TimeZoneInfo.Utc).Report!;

        var project = Assert.Single(report.Projects);
        Assert.Equal("gamma", project.Name);
        Assert.Equal(((300_000 * 10.0) + (1000 * 45.0)) / 1_000_000, project.Cost, 9);
        Assert.Equal(1, report.LongContextRequests);
        Assert.Equal("gpt-5.5", Assert.Single(report.Models).Name);
    }

    // ----- Grok -----

    [Fact]
    public void Grok_credentials_prefer_the_oidc_entry_with_a_token()
    {
        var credentials = GrokProvider.ParseCredentials("""
            {
              "https://accounts.x.ai/sign-in": {"key":"legacy","auth_mode":"session"},
              "https://auth.x.ai::client": {"key":"oidc-token","auth_mode":"oidc","email":"dev@example.com","expires_at":"2026-11-20T00:00:00Z"},
              "https://auth.x.ai::stale": {"key":""}
            }
            """);

        Assert.Equal("oidc-token", credentials.AccessToken);
        Assert.Equal("dev@example.com", credentials.Email);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), credentials.ExpiresAt);
    }

    [Fact]
    public void Grok_usage_maps_weekly_credits_products_and_wallet()
    {
        const string body = """
            {"config":{"creditUsagePercent":17.0,
              "currentPeriod":{"start":"2026-11-10T00:00:00Z","end":"2026-11-17T00:00:00Z"},
              "productUsage":[{"product":"GrokChat","usagePercent":3.0},{"product":"GrokBuild","usagePercent":14.0},{"product":"GrokImagine","usagePercent":0}],
              "prepaidBalance":{"val":1446},"subscriptionTier":"SUPER_GROK_HEAVY"}}
            """;

        var snapshot = GrokProvider.ParseUsage(body, new GrokCredentials("t", null, "dev@example.com", "oidc"), null, Now);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("Weekly", window.Title);
        Assert.Equal(17, window.UsedPercent);
        Assert.Equal(10080, window.WindowMinutes);
        Assert.Equal(["Grok Build", "Grok Chat", "Prepaid balance"], snapshot.Details.Select(row => row.Label));
        Assert.Equal("SuperGrok Heavy", snapshot.Plan);
        Assert.Equal("dev@example.com", snapshot.Account);
    }

    [Fact]
    public void Grok_usage_drops_shares_that_do_not_add_up_and_rejects_a_period_only_answer()
    {
        var mismatched = GrokProvider.ParseUsage(
            """{"config":{"creditUsagePercent":40,"productUsage":[{"product":"GrokBuild","usagePercent":5}]}}""",
            new GrokCredentials("t", null, null, "oidc"),
            "SuperGrok",
            Now);
        Assert.Empty(mismatched.Details);
        Assert.Equal("Credits", mismatched.Windows[0].Title);

        var error = Assert.Throws<ProviderException>(() => GrokProvider.ParseUsage(
            """{"config":{"currentPeriod":{"start":"2026-11-10T00:00:00Z","end":"2026-11-17T00:00:00Z"}}}""",
            new GrokCredentials("t", null, null, "oidc"),
            null,
            Now));
        Assert.Equal(ProviderErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task Grok_expired_cli_sign_in_fails_before_any_request()
    {
        this.Write(".grok/auth.json", """{"https://auth.x.ai::c":{"key":"tok","expires_at":"2026-11-01T00:00:00Z"}}""");
        var requests = 0;
        var context = this.NewContext(_ =>
        {
            requests++;
            return Json("{}");
        });

        var error = await Assert.ThrowsAsync<ProviderException>(() => new GrokProvider().FetchAsync(context, CancellationToken.None));

        Assert.Equal(ProviderErrorKind.Unauthorized, error.Kind);
        Assert.Equal(0, requests);
    }

    // ----- Gemini -----

    [Fact]
    public void Gemini_usage_groups_models_into_lanes_by_lowest_remaining()
    {
        const string body = """
            {"buckets":[
              {"modelId":"gemini-2.5-pro","remainingFraction":0.9,"resetTime":"2026-11-14T00:00:00Z"},
              {"modelId":"gemini-2.5-pro","remainingFraction":0.6,"resetTime":"2026-11-14T01:00:00Z"},
              {"modelId":"gemini-2.5-flash","remainingFraction":1.0},
              {"modelId":"gemini-2.5-flash-lite","remainingFraction":0.25},
              {"modelId":"broken"}
            ]}
            """;

        var snapshot = GeminiProvider.ParseUsage(body, "dev@example.com", "Workspace", Now);

        Assert.Equal(["Pro models", "Flash models", "Flash Lite models"], snapshot.Windows.Select(window => window.Title));
        Assert.Equal([40, 0, 75], snapshot.Windows.Select(window => Math.Round(window.UsedPercent)));
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero), snapshot.Windows[0].ResetsAt);
    }

    [Fact]
    public void Gemini_code_assist_resolves_plan_project_and_shutdown_flag()
    {
        var paid = GeminiProvider.ParseCodeAssist(
            """{"cloudaicompanionProject":"proj-1","currentTier":{"id":"free-tier"},"paidTier":{"name":"Code Assist Standard"}}""",
            null);
        Assert.Equal(("proj-1", "Code Assist Standard"), (paid.ProjectId, paid.Plan));

        Assert.Equal("Workspace", GeminiProvider.ParseCodeAssist("""{"currentTier":{"id":"free-tier"}}""", "example.com").Plan);
        Assert.Equal("Free", GeminiProvider.ParseCodeAssist("""{"currentTier":{"id":"free-tier"}}""", null).Plan);

        var shutDown = GeminiProvider.ParseCodeAssist(
            """{"ineligibleTiers":[{"tierId":"free-tier","reasonCode":"UNSUPPORTED_CLIENT"}]}""",
            null);
        Assert.True(shutDown.ConsumerUnsupported);
        Assert.Null(shutDown.TierId);
    }

    [Fact]
    public void Gemini_oauth_client_is_extracted_from_cli_source()
    {
        var found = GeminiProvider.ExtractOAuthClient(
            "const OAUTH_CLIENT_ID = 'example-id.apps.example.test';\nconst OAUTH_CLIENT_SECRET = \"example-secret_1\";");

        Assert.Equal(("example-id.apps.example.test", "example-secret_1"), found);
        Assert.Null(GeminiProvider.ExtractOAuthClient("nothing here"));
    }

    [Fact]
    public async Task Gemini_renews_an_expired_token_in_memory_and_never_rewrites_the_cli_file()
    {
        var expired = Now.AddHours(-1).ToUnixTimeMilliseconds();
        var idToken = Jwt("""{"email":"dev@example.com","hd":"example.com"}""");
        var credentialsJson = $$"""{"access_token":"old","refresh_token":"refresh-1","expiry_date":{{expired}},"id_token":"{{idToken}}"}""";
        var path = this.Write(".gemini/oauth_creds.json", credentialsJson);
        var bearerTokens = new List<string?>();
        var tokenRequests = 0;
        var context = this.NewContext(
            request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("oauth2.googleapis.com", StringComparison.Ordinal))
                {
                    tokenRequests++;
                    return Json("""{"access_token":"fresh","expires_in":3600}""");
                }

                bearerTokens.Add(request.Headers.Authorization?.Parameter);
                return url.Contains("loadCodeAssist", StringComparison.Ordinal)
                    ? Json("""{"cloudaicompanionProject":"proj-1","currentTier":{"id":"standard-tier"}}""")
                    : Json("""{"buckets":[{"modelId":"gemini-2.5-pro","remainingFraction":0.5}]}""");
            },
            environment: name => name switch
            {
                "GEMINI_OAUTH_CLIENT_ID" => "example-id",
                "GEMINI_OAUTH_CLIENT_SECRET" => "example-secret",
                _ => null,
            });
        var provider = new GeminiProvider();

        var first = await provider.FetchAsync(context, CancellationToken.None);
        await provider.FetchAsync(context, CancellationToken.None);

        Assert.Equal(50, first.Windows[0].UsedPercent);
        Assert.Equal("Paid", first.Plan);
        Assert.Equal("dev@example.com", first.Account);
        Assert.All(bearerTokens, token => Assert.Equal("fresh", token));
        Assert.Equal(1, tokenRequests);
        Assert.Equal(credentialsJson, File.ReadAllText(path));
    }

    [Fact]
    public async Task Gemini_reports_the_consumer_shutdown()
    {
        var future = Now.AddHours(1).ToUnixTimeMilliseconds();
        this.Write(".gemini/oauth_creds.json", $$"""{"access_token":"tok","expiry_date":{{future}}}""");
        var context = this.NewContext(_ => Json("""{"ineligibleTiers":[{"reasonCode":"UNSUPPORTED_CLIENT"}]}"""));

        var error = await Assert.ThrowsAsync<ProviderException>(() => new GeminiProvider().FetchAsync(context, CancellationToken.None));

        Assert.Equal(GeminiProvider.ConsumerShutdownMessage, error.Message);
    }

    // ----- Cursor -----

    [Fact]
    public void Cursor_session_cookie_is_built_from_the_app_token()
    {
        var token = Jwt($$"""{"sub":"auth0|user_01ABC","exp":{{Now.AddDays(1).ToUnixTimeSeconds()}}}""");

        var session = CursorProvider.SessionFromAccessToken(token, Now);

        Assert.Equal($"WorkosCursorSessionToken=user_01ABC%3A%3A{token}", session.CookieHeader);
        Assert.Equal("user_01ABC", session.UserId);

        var expired = Jwt($$"""{"sub":"auth0|user_01ABC","exp":{{Now.AddSeconds(30).ToUnixTimeSeconds()}}}""");
        Assert.Equal(
            ProviderErrorKind.Unauthorized,
            Assert.Throws<ProviderException>(() => CursorProvider.SessionFromAccessToken(expired, Now)).Kind);
        Assert.Throws<ProviderException>(() => CursorProvider.SessionFromAccessToken(Jwt("""{"sub":"auth0|bad id;x","exp":9999999999}"""), Now));
    }

    [Fact]
    public void Cursor_usage_maps_plan_lanes_grok_bot_and_on_demand_spend()
    {
        const string summary = """
            {"billingCycleStart":"2026-11-01T00:00:00Z","billingCycleEnd":"2026-12-01T00:00:00Z","membershipType":"pro_plus",
             "individualUsage":{"plan":{"used":920,"limit":2000,"autoPercentUsed":0.36,"apiPercentUsed":61.5,"totalPercentUsed":46},
                                "onDemand":{"used":420,"limit":2000}}}
            """;
        const string grokBot = """{"usagePercent":12.5,"includedLimitZero":false,"nextResetTimestampUtc":"2026-11-16T00:00:00Z"}""";

        var snapshot = CursorProvider.ParseUsage(summary, """{"email":"dev@example.com","sub":"user_1"}""", grokBot, null, Now);

        Assert.Equal(["Plan usage · Monthly", "Auto models", "Named models", "Grok Bot"], snapshot.Windows.Select(window => window.Title));
        Assert.Equal([46, 0.36, 61.5, 12.5], snapshot.Windows.Select(window => window.UsedPercent));
        Assert.Equal(43200, snapshot.Windows[0].WindowMinutes);
        Assert.Equal("Pro+", snapshot.Plan);
        Assert.Equal("dev@example.com", snapshot.Account);
        var spend = Assert.Single(snapshot.Details);
        Assert.Equal("On-demand", spend.Label);
        Assert.Equal(0.21, spend.Progress!.Value, 6);
    }

    [Fact]
    public void Cursor_unified_weekly_pool_and_legacy_request_quota_replace_the_monthly_lanes()
    {
        const string summary = """{"billingCycleEnd":"2026-12-01T00:00:00Z","individualUsage":{"plan":{"totalPercentUsed":80}}}""";
        const string unified = """{"usagePercent":33,"includedLimitZero":false,"isUnified":true,"nextResetTimestampUtc":"2026-11-16T00:00:00Z"}""";

        var weekly = Assert.Single(CursorProvider.ParseUsage(summary, null, unified, null, Now).Windows);
        Assert.Equal(("Weekly", 33d, 10080), (weekly.Title, weekly.UsedPercent, weekly.WindowMinutes!.Value));

        var requests = Assert.Single(CursorProvider.ParseUsage(
            summary,
            null,
            unified,
            """{"gpt-4":{"numRequestsTotal":125,"maxRequestUsage":500}}""",
            Now).Windows);
        Assert.Equal(("Requests · Monthly", 25d, "125 / 500 requests"), (requests.Title, requests.UsedPercent, requests.Note));
    }

    [Fact]
    public async Task Cursor_reads_the_app_token_from_its_state_database()
    {
        var token = Jwt($$"""{"sub":"auth0|user_01ABC","exp":{{Now.AddDays(1).ToUnixTimeSeconds()}}}""");
        var appData = Path.Combine(this.directory, "AppData");
        var database = Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        TestSqlite.Execute(
            database,
            "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value BLOB);"
            + $"INSERT INTO ItemTable VALUES ('other', 'x'), ('cursorAuth/accessToken', '{token}');");

        Assert.Equal(token, WinSqlite.DecodeText(WinSqlite.ReadSingleValue(database, "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'")!));
        Assert.Null(WinSqlite.ReadSingleValue(database, "SELECT value FROM ItemTable WHERE key = 'missing'"));
        Assert.Equal("tok", WinSqlite.DecodeText(Encoding.Unicode.GetBytes("tok")));

        string? cookie = null;
        var context = this.NewContext(
            request =>
            {
                cookie ??= request.Headers.GetValues("Cookie").Single();
                return request.RequestUri!.AbsolutePath == "/api/usage-summary"
                    ? Json("""{"membershipType":"pro","individualUsage":{"plan":{"totalPercentUsed":10}}}""")
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            },
            environment: name => name == "APPDATA" ? appData : null);

        var snapshot = await new CursorProvider().FetchAsync(context, CancellationToken.None);

        Assert.Equal($"WorkosCursorSessionToken=user_01ABC%3A%3A{token}", cookie);
        Assert.Equal(10, snapshot.Windows[0].UsedPercent);
        Assert.Equal("Pro", snapshot.Plan);
    }

    [Fact]
    public async Task Cursor_pasted_cookie_overrides_the_app_and_a_rejection_names_it()
    {
        var context = this.NewContext(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            secrets: id => id == "cursor" ? "Cookie: WorkosCursorSessionToken=abc; other=1" : null);

        var error = await Assert.ThrowsAsync<ProviderException>(() => new CursorProvider().FetchAsync(context, CancellationToken.None));

        Assert.Equal(ProviderErrorKind.Unauthorized, error.Kind);
        Assert.Contains("pasted cookie", error.Message, StringComparison.Ordinal);
    }

    // ----- Token refresh -----

    [Fact]
    public async Task Claude_expired_token_is_not_renewed_unless_the_user_opted_in()
    {
        var expired = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var original = $$$"""{"claudeAiOauth":{"accessToken":"old","refreshToken":"r1","expiresAt":{{{expired}}}}}""";
        var path = this.Write(".claude/.credentials.json", original);
        var requests = 0;
        var context = this.NewContext(_ =>
        {
            requests++;
            return Json("{}");
        });

        var error = await Assert.ThrowsAsync<ProviderException>(() => new ClaudeProvider().FetchAsync(context, CancellationToken.None));

        Assert.Equal(ProviderErrorKind.Unauthorized, error.Kind);
        Assert.Equal(0, requests);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task Claude_opt_in_renewal_writes_new_tokens_back_and_keeps_other_fields()
    {
        var expired = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var path = this.Write(
            ".claude/.credentials.json",
            $$$"""{"claudeAiOauth":{"accessToken":"old","refreshToken":"r1","expiresAt":{{{expired}}},"subscriptionType":"max","scopes":["user:profile"]},"mcpOAuth":{"keep":true}}""");
        string? usageBearer = null;
        var context = this.NewContext(
            request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                {
                    return Json("""{"access_token":"new","refresh_token":"r2","expires_in":3600}""");
                }

                usageBearer = request.Headers.Authorization?.Parameter;
                return Json("""{"five_hour":{"utilization":12,"resets_at":"2026-11-13T15:00:00Z"}}""");
            },
            allowTokenRefresh: true);

        var snapshot = await new ClaudeProvider().FetchAsync(context, CancellationToken.None);

        Assert.Equal("new", usageBearer);
        Assert.Equal(12, snapshot.Windows[0].UsedPercent);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        var oauth = saved.RootElement.GetProperty("claudeAiOauth");
        Assert.Equal("new", oauth.GetProperty("accessToken").GetString());
        Assert.Equal("r2", oauth.GetProperty("refreshToken").GetString());
        Assert.Equal(Now.AddHours(1).ToUnixTimeMilliseconds(), oauth.GetProperty("expiresAt").GetInt64());
        Assert.Equal("max", oauth.GetProperty("subscriptionType").GetString());
        Assert.True(saved.RootElement.GetProperty("mcpOAuth").GetProperty("keep").GetBoolean());
    }

    [Fact]
    public async Task Codex_opt_in_renewal_retries_after_a_rejection_and_updates_auth_json()
    {
        var path = this.Write(
            ".codex/auth.json",
            """{"OPENAI_API_KEY":null,"tokens":{"access_token":"old","refresh_token":"r1","account_id":"acct"}}""");
        var bearers = new List<string?>();
        var context = this.NewContext(
            request =>
            {
                if (request.RequestUri!.Host == "auth.openai.com")
                {
                    return Json("""{"access_token":"new","refresh_token":"r2","id_token":"id2"}""");
                }

                bearers.Add(request.Headers.Authorization?.Parameter);
                return bearers.Count == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":18000}}}""");
            },
            allowTokenRefresh: true);

        var snapshot = await new CodexProvider().FetchAsync(context, CancellationToken.None);

        Assert.Equal(["old", "new"], bearers);
        Assert.Equal(5, snapshot.Windows[0].UsedPercent);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        var tokens = saved.RootElement.GetProperty("tokens");
        Assert.Equal(("new", "r2", "id2", "acct"), (
            tokens.GetProperty("access_token").GetString(),
            tokens.GetProperty("refresh_token").GetString(),
            tokens.GetProperty("id_token").GetString(),
            tokens.GetProperty("account_id").GetString()));
        Assert.Equal("2026-11-13T12:00:00Z", saved.RootElement.GetProperty("last_refresh").GetString());
    }

    [Fact]
    public async Task Renewal_does_not_overwrite_tokens_the_cli_changed_meanwhile()
    {
        var path = this.Write(".codex/auth.json", """{"tokens":{"access_token":"old","refresh_token":"r1"}}""");
        const string fromCli = """{"tokens":{"access_token":"cli-new","refresh_token":"cli-r2"}}""";
        var context = this.NewContext(_ =>
        {
            // The CLI renews its own tokens while CodexBar's request is in flight.
            File.WriteAllText(path, fromCli);
            return Json("""{"access_token":"ours","refresh_token":"ours-r2"}""");
        });

        Assert.True(await TokenRefresher.RefreshCodexAsync(context, path, CancellationToken.None));

        Assert.Equal(fromCli, File.ReadAllText(path));
    }

    // ----- Alerts -----

    [Fact]
    public async Task Alerts_use_configured_thresholds_and_report_only_the_highest_crossed()
    {
        var provider = new FakeProvider();
        var config = this.NewConfig();
        config.Update(settings => settings.AlertThresholds = [75, 50, 90, 250]);
        var store = new UsageStore(this.NewContext(), config, [provider]);
        var alerts = new List<UsageAlert>();
        store.Alert += alerts.Add;

        foreach (var percent in new double[] { 10, 80, 85, 95 })
        {
            provider.Next = () => FakeProvider.Snapshot(percent);
            await store.RefreshAllAsync(CancellationToken.None);
        }

        // 10→80 crosses 50 and 75 (one alert, for 75); 80→85 crosses nothing; 85→95 crosses 90.
        Assert.Equal([75, 90], alerts.Select(alert => alert.Threshold!.Value));
    }

    [Fact]
    public async Task Reset_and_sign_in_alerts_fire_only_when_switched_on_and_after_a_baseline()
    {
        var provider = new FakeProvider();
        var config = this.NewConfig();
        config.Update(settings => settings.NotifyReset = true);
        var store = new UsageStore(this.NewContext(), config, [provider]);
        var alerts = new List<UsageAlert>();
        store.Alert += alerts.Add;

        provider.Next = () => FakeProvider.Snapshot(95, Now.AddHours(1));
        await store.RefreshAllAsync(CancellationToken.None);
        provider.Next = () => FakeProvider.Snapshot(2, Now.AddHours(6));
        await store.RefreshAllAsync(CancellationToken.None);
        provider.Next = () => throw new ProviderException(ProviderErrorKind.Unauthorized, "expired");
        await store.RefreshAllAsync(CancellationToken.None);
        await store.RefreshAllAsync(CancellationToken.None);

        Assert.Equal([AlertKind.Reset, AlertKind.SignInExpired], alerts.Select(alert => alert.Kind));
    }

    private ConfigStore NewConfig() => new(Path.Combine(this.directory, "config.json"), new PlainProtector());

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plainText) => plainText;

        public string? Unprotect(string protectedText) => protectedText;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }

    private sealed class FakeProvider : IUsageProvider
    {
        public Func<UsageSnapshot> Next { get; set; } = () => Snapshot(0);

        public string Id => "fake";

        public string DisplayName => "Fake";

        public string DashboardUrl => "https://example.com";

        public string SetupHint => "none";

        public bool UsesApiKey => false;

        public string? StatusPageUrl { get; init; }

        public static UsageSnapshot Snapshot(double percent, DateTimeOffset? resetsAt = null) =>
            new("fake", [new RateWindow("w", "Weekly", percent, 10080, resetsAt)], [], null, null, Now);

        public bool HasCredentials(ProviderContext context) => true;

        public Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken) =>
            Task.FromResult(this.Next());
    }

    /// <summary>Creates a throwaway SQLite database through the same Windows library the app reads with.</summary>
    private static class TestSqlite
    {
        public static void Execute(string path, string sql)
        {
            Assert.Equal(0, sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out var database, 0x2 | 0x4, IntPtr.Zero));
            try
            {
                Assert.Equal(0, sqlite3_exec(database, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            }
            finally
            {
                sqlite3_close(database);
            }
        }

        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_exec(IntPtr database, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_close(IntPtr database);
    }
}
