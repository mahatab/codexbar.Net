using System.Net;
using CodexBar.Core;
using CodexBar.Core.Providers;

namespace CodexBar.Core.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "codexbar-tests-" + Guid.NewGuid().ToString("N"));

    public StoreTests() => Directory.CreateDirectory(this.directory);

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    private ConfigStore NewConfig() => new(Path.Combine(this.directory, "config.json"), new ReversingProtector());

    private ProviderContext NewContext(HttpMessageHandler? handler = null, Func<string, string?>? secrets = null) =>
        new(new HttpClient(handler ?? new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), this.directory, secrets: secrets);

    [Fact]
    public void Secrets_are_stored_protected_and_round_trip()
    {
        var store = this.NewConfig();
        store.SetSecret("openrouter", "  sk-or-secret  ");

        var onDisk = File.ReadAllText(Path.Combine(this.directory, "config.json"));
        Assert.DoesNotContain("sk-or-secret", onDisk);
        Assert.Equal("sk-or-secret", this.NewConfig().GetSecret("openrouter"));

        store.SetSecret("openrouter", "");
        Assert.Null(this.NewConfig().GetSecret("openrouter"));
    }

    [Fact]
    public void Unreadable_config_is_reported_and_never_overwritten()
    {
        var path = Path.Combine(this.directory, "config.json");
        File.WriteAllText(path, "{ not json");

        var store = this.NewConfig();
        store.Update(config => config.RefreshSeconds = 120);

        Assert.NotNull(store.LoadError);
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void Refresh_interval_has_a_floor()
    {
        var store = this.NewConfig();
        store.Update(config => config.RefreshSeconds = 5);
        Assert.Equal(AppConfig.MinRefreshSeconds, store.RefreshSeconds);
    }

    [Fact]
    public void Providers_default_to_on_only_when_credentials_exist()
    {
        var config = this.NewConfig();
        var store = new UsageStore(this.NewContext(), config);
        Assert.Empty(store.EnabledProviders());

        Directory.CreateDirectory(Path.Combine(this.directory, ".codex"));
        File.WriteAllText(Path.Combine(this.directory, ".codex", "auth.json"), "{}");
        Assert.Equal(["codex"], store.EnabledProviders().Select(p => p.Id));

        config.Update(c => c.Providers["codex"] = false);
        Assert.Empty(store.EnabledProviders());
    }

    [Fact]
    public async Task Failed_refresh_keeps_the_last_reading_as_stale()
    {
        var provider = new FakeProvider();
        var store = new UsageStore(this.NewContext(), this.NewConfig(), [provider]);

        provider.Next = () => FakeProvider.Snapshot(40);
        await store.RefreshAllAsync(CancellationToken.None);
        provider.Next = () => throw new ProviderException(ProviderErrorKind.Network, "offline");
        await store.RefreshAllAsync(CancellationToken.None);

        var state = store.State("fake");
        Assert.Equal("offline", state.Error);
        Assert.Equal(40, state.Snapshot!.Windows[0].UsedPercent);
        Assert.False(state.IsRefreshing);
    }

    [Fact]
    public async Task High_usage_alert_fires_once_on_upward_crossing_only()
    {
        var provider = new FakeProvider();
        var store = new UsageStore(this.NewContext(), this.NewConfig(), [provider]);
        var alerts = new List<UsageAlert>();
        store.Alert += alerts.Add;

        foreach (var percent in new double[] { 95, 50, 91, 97 })
        {
            provider.Next = () => FakeProvider.Snapshot(percent);
            await store.RefreshAllAsync(CancellationToken.None);
        }

        // 95 is the baseline (no alert), 50→91 crosses, 91→97 stays above.
        var alert = Assert.Single(alerts);
        Assert.Equal(AlertKind.Threshold, alert.Kind);
        Assert.Equal(91, alert.Window!.UsedPercent);
    }

    [Fact]
    public async Task Codex_fetch_sends_bearer_and_account_headers_and_maps_401()
    {
        Directory.CreateDirectory(Path.Combine(this.directory, ".codex"));
        File.WriteAllText(
            Path.Combine(this.directory, ".codex", "auth.json"),
            """{"tokens":{"access_token":"tok-123","account_id":"acct-9"}}""");
        HttpRequestMessage? seen = null;
        var status = HttpStatusCode.OK;
        var handler = new StubHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"rate_limit":{"primary_window":{"used_percent":10,"reset_at":1794571200,"limit_window_seconds":18000}}}"""),
            };
        });
        var provider = new CodexProvider();

        var snapshot = await provider.FetchAsync(this.NewContext(handler), CancellationToken.None);

        Assert.Single(snapshot.Windows);
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", seen!.RequestUri!.ToString());
        Assert.Equal("Bearer tok-123", seen.Headers.Authorization!.ToString());
        Assert.Equal("acct-9", seen.Headers.GetValues("ChatGPT-Account-Id").Single());

        status = HttpStatusCode.Unauthorized;
        var error = await Assert.ThrowsAsync<ProviderException>(
            () => provider.FetchAsync(this.NewContext(handler), CancellationToken.None));
        Assert.Equal(ProviderErrorKind.Unauthorized, error.Kind);
    }

    [Fact]
    public async Task Claude_expired_token_fails_before_any_request()
    {
        Directory.CreateDirectory(Path.Combine(this.directory, ".claude"));
        File.WriteAllText(
            Path.Combine(this.directory, ".claude", ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1000}}""");
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => new ClaudeProvider().FetchAsync(this.NewContext(handler), CancellationToken.None));

        Assert.Equal(ProviderErrorKind.Unauthorized, error.Kind);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Copilot_device_flow_waits_through_pending_and_slow_down()
    {
        var replies = new Queue<string>(
        [
            """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
            """{"error":"authorization_pending"}""",
            """{"error":"slow_down"}""",
            """{"access_token":"gho_token","token_type":"bearer","scope":"read:user"}""",
        ]);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies.Dequeue()) });
        var waits = new List<double>();
        var flow = new CopilotDeviceFlow(new HttpClient(handler), (span, _) =>
        {
            waits.Add(span.TotalSeconds);
            return Task.CompletedTask;
        });

        var code = await flow.RequestDeviceCodeAsync(CancellationToken.None);
        var token = await flow.PollForTokenAsync(code, CancellationToken.None);

        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("gho_token", token);
        Assert.Equal([5, 5, 10], waits);
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plainText) => "enc:" + new string(plainText.Reverse().ToArray());

        public string? Unprotect(string protectedText) =>
            protectedText.StartsWith("enc:", StringComparison.Ordinal) ? new string(protectedText[4..].Reverse().ToArray()) : null;
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

        public static UsageSnapshot Snapshot(double percent) =>
            new("fake", [new RateWindow("w", "Weekly", percent)], [], null, null, DateTimeOffset.UnixEpoch);

        public bool HasCredentials(ProviderContext context) => true;

        public Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken) =>
            Task.FromResult(this.Next());
    }
}
