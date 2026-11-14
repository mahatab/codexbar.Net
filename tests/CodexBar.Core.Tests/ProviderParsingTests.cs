using System.Text;
using CodexBar.Core;
using CodexBar.Core.Providers;

namespace CodexBar.Core.Tests;

public class ProviderParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static string Jwt(string payloadJson) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Fact]
    public void Codex_credentials_read_token_account_and_email()
    {
        var idToken = Jwt("""{"email":"dev@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"acct-from-jwt"}}""");
        var json = """{"tokens":{"access_token":"tok","id_token":"ID","account_id":"acct-1"}}""".Replace("ID", idToken);

        var credentials = CodexProvider.ParseCredentials(json);

        Assert.Equal("tok", credentials.AccessToken);
        Assert.Equal("acct-1", credentials.AccountId);
        Assert.Equal("dev@example.com", credentials.Email);
    }

    [Fact]
    public void Codex_credentials_fall_back_to_jwt_account_id()
    {
        var idToken = Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-from-jwt"}}""");
        var credentials = CodexProvider.ParseCredentials(
            """{"tokens":{"access_token":"tok","id_token":"ID"}}""".Replace("ID", idToken));

        Assert.Equal("acct-from-jwt", credentials.AccountId);
        Assert.Null(credentials.Email);
    }

    [Fact]
    public void Codex_api_key_only_login_is_not_configured()
    {
        var error = Assert.Throws<ProviderException>(
            () => CodexProvider.ParseCredentials("""{"OPENAI_API_KEY":"sk-test","tokens":null}"""));
        Assert.Equal(ProviderErrorKind.NotConfigured, error.Kind);
    }

    [Fact]
    public void Codex_usage_maps_session_weekly_extra_limits_and_credits()
    {
        const string body = """
        {
          "plan_type": "free_workspace",
          "rate_limit": {
            "primary_window": {"used_percent": 42, "reset_at": 1794571200, "limit_window_seconds": 18000},
            "secondary_window": {"used_percent": 7, "reset_at": 1795000000, "limit_window_seconds": 604800}
          },
          "additional_rate_limits": [
            {"limit_name": "Spark", "rate_limit": {"primary_window": {"used_percent": 3, "reset_at": 1794571200, "limit_window_seconds": 18000}}},
            "malformed"
          ],
          "credits": {"has_credits": true, "unlimited": false, "balance": "1250"}
        }
        """;

        var snapshot = CodexProvider.ParseUsage(body, new CodexCredentials("tok", null, "dev@example.com"), Now);

        Assert.Equal("Free Workspace", snapshot.Plan);
        Assert.Equal("dev@example.com", snapshot.Account);
        Assert.Collection(
            snapshot.Windows,
            session =>
            {
                Assert.Equal("Session (5h)", session.Title);
                Assert.Equal(42, session.UsedPercent);
                Assert.Equal(300, session.WindowMinutes);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794571200), session.ResetsAt);
            },
            weekly => Assert.Equal("Weekly", weekly.Title),
            spark => Assert.Equal("Spark · Session (5h)", spark.Title));
        Assert.Contains(snapshot.Details, row => row.Label == "Credits");
    }

    [Fact]
    public void Codex_usage_without_windows_or_credits_is_empty_not_an_error()
    {
        var snapshot = CodexProvider.ParseUsage("""{"plan_type":"plus","credits":{"has_credits":false,"balance":0}}""", new CodexCredentials("t", null, null), Now);

        Assert.Empty(snapshot.Windows);
        Assert.Empty(snapshot.Details);
        Assert.Equal("Plus", snapshot.Plan);
    }

    [Fact]
    public void Claude_credentials_read_expiry_in_milliseconds()
    {
        var credentials = ClaudeProvider.ParseCredentials(
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1794571200000,"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794571200), credentials.ExpiresAt);
        Assert.Equal("max", credentials.SubscriptionType);
    }

    [Fact]
    public void Claude_mcp_only_credentials_are_not_configured()
    {
        var error = Assert.Throws<ProviderException>(() => ClaudeProvider.ParseCredentials("""{"mcpOAuth":{}}"""));
        Assert.Equal(ProviderErrorKind.NotConfigured, error.Kind);
    }

    [Fact]
    public void Claude_usage_maps_windows_scoped_limits_and_extra_usage()
    {
        const string body = """
        {
          "five_hour": {"utilization": 12.5, "resets_at": "2026-11-13T15:00:00.000000+00:00"},
          "seven_day": {"utilization": 61, "resets_at": "2026-11-18T08:00:00Z"},
          "seven_day_sonnet": {"utilization": 4, "resets_at": null},
          "seven_day_opus": null,
          "limits": [
            {"group": "weekly", "kind": "weekly_scoped", "percent": 33, "resets_at": "2026-11-18T08:00:00Z",
             "scope": {"model": {"id": "claude-fable", "display_name": "Fable"}}},
            {"group": "weekly", "kind": "weekly_scoped", "percent": true,
             "scope": {"model": {"id": "claude-x", "display_name": "Ignored"}}},
            {"group": "session", "kind": "session", "percent": 5}
          ],
          "extra_usage": {"is_enabled": true, "monthly_limit": 5000, "used_credits": 1250, "currency": "USD"}
        }
        """;
        var credentials = new ClaudeCredentials("tok", null, "max", "default_claude_max_20x");

        var snapshot = ClaudeProvider.ParseUsage(body, credentials, Now);

        Assert.Equal("Max 20x", snapshot.Plan);
        Assert.Equal(
            ["claude-session", "claude-weekly", "claude-weekly-sonnet", "claude-weekly-scoped-claude-fable"],
            snapshot.Windows.Select(w => w.Id));
        Assert.Equal(12.5, snapshot.Windows[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero), snapshot.Windows[0].ResetsAt);
        Assert.Equal("Fable only · Weekly", snapshot.Windows[3].Title);
        var extra = Assert.Single(snapshot.Details);
        Assert.Equal(0.25, extra.Progress);
    }

    [Fact]
    public void Claude_missing_session_stays_absent_and_weekly_falls_back_to_oauth_apps()
    {
        var snapshot = ClaudeProvider.ParseUsage(
            """{"five_hour": null, "seven_day_oauth_apps": {"utilization": 9}}""",
            new ClaudeCredentials("tok", null, null, "default_claude_pro"),
            Now);

        var weekly = Assert.Single(snapshot.Windows);
        Assert.Equal("claude-weekly", weekly.Id);
        Assert.Equal("Pro", snapshot.Plan);
    }

    [Fact]
    public void Copilot_usage_maps_premium_and_chat()
    {
        const string body = """
        {
          "copilot_plan": "individual",
          "quota_reset_date": "2026-12-01",
          "quota_snapshots": {
            "premium_interactions": {"entitlement": 300, "remaining": 75, "percent_remaining": 25, "unlimited": false},
            "chat": {"entitlement": 0, "remaining": 0, "percent_remaining": 100, "unlimited": true}
          }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(body, Now);

        var premium = Assert.Single(snapshot.Windows);
        Assert.Equal(75, premium.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), premium.ResetsAt);
        Assert.Equal("Individual", snapshot.Plan);
    }

    [Fact]
    public void Copilot_derives_percent_and_drops_token_billing_placeholders()
    {
        const string body = """
        {
          "copilot_plan": "business",
          "token_based_billing": true,
          "quota_snapshots": {
            "premium_interactions": {"entitlement": 0, "remaining": 0, "percent_remaining": 100, "credits_used": 31},
            "chat": {"entitlement": 200, "remaining": 50}
          }
        }
        """;

        var snapshot = CopilotProvider.ParseUsage(body, Now);

        var chat = Assert.Single(snapshot.Windows);
        Assert.Equal("copilot-chat", chat.Id);
        Assert.Equal(75, chat.UsedPercent);
        Assert.Contains(snapshot.Details, row => row.Label == "Credits used");
    }

    [Fact]
    public void Copilot_ended_subscription_is_reported()
    {
        var error = Assert.Throws<ProviderException>(
            () => CopilotProvider.ParseUsage("""{"access_type_sku":"subscription_ended","quota_snapshots":{}}""", Now));
        Assert.Contains("subscription ended", error.Message);
    }

    [Fact]
    public void OpenRouter_maps_balance_and_key_limit()
    {
        var snapshot = OpenRouterProvider.ParseUsage(
            """{"data":{"total_credits":20,"total_usage":5}}""",
            """{"data":{"limit":10,"limit_remaining":7.5,"usage":99,"limit_reset":"monthly","usage_monthly":2.5}}""",
            Now);

        var limit = Assert.Single(snapshot.Windows);
        Assert.Equal(25, limit.UsedPercent);
        Assert.Equal("Key limit · Monthly", limit.Title);
        Assert.Equal(["Balance", "Account usage", "This month (key)"], snapshot.Details.Select(d => d.Label));
        Assert.Equal(0.25, snapshot.Details[1].Progress);
    }

    [Fact]
    public void OpenRouter_without_any_data_is_an_invalid_response()
    {
        var error = Assert.Throws<ProviderException>(() => OpenRouterProvider.ParseUsage("""{"data":{}}""", null, Now));
        Assert.Equal(ProviderErrorKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData(90, "Resets in 1h 30m")]
    [InlineData(45, "Resets in 45m")]
    [InlineData(60 * 24 * 3 + 120, "Resets in 3d 2h")]
    [InlineData(-5, "Resetting now")]
    public void Reset_countdown_formats(int minutesAhead, string expected) =>
        Assert.Equal(expected, UsageFormatter.ResetCountdown(Now.AddMinutes(minutesAhead), Now));

    [Theory]
    [InlineData(300, "Session (5h)")]
    [InlineData(10080, "Weekly")]
    [InlineData(43200, "Monthly")]
    [InlineData(1440, "1-day window")]
    [InlineData(null, "Usage")]
    public void Window_titles_follow_length(int? minutes, string expected) =>
        Assert.Equal(expected, UsageFormatter.WindowTitle(minutes));
}
