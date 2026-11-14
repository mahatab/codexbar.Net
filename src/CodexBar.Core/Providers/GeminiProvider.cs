using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBar.Core.Providers;

/// <summary>
/// Gemini CLI quotas through the Cloud Code API, a reduced port of <c>GeminiStatusProbe.swift</c>.
/// Reads the Gemini CLI's <c>oauth_creds.json</c> and never writes to it. An expired access token is renewed
/// in memory only: Google does not rotate the refresh token, so this cannot sign the CLI out.
/// </summary>
public sealed partial class GeminiProvider : IUsageProvider
{
    public const string ConsumerShutdownMessage =
        "Google no longer supports Gemini CLI sign-in for individual, AI Pro or Ultra accounts (since June 2026). "
        + "Workspace, education and Code Assist Standard/Enterprise accounts still work.";

    private const string QuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string LoadCodeAssistUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string ProjectsUrl = "https://cloudresourcemanager.googleapis.com/v1/projects";

    private readonly object gate = new();
    private (string RefreshToken, string AccessToken, DateTimeOffset ExpiresAt)? renewed;

    public string Id => "gemini";

    public string DisplayName => "Gemini";

    public string DashboardUrl => "https://gemini.google.com/usage";

    public string SetupHint => "Sign in with the Gemini CLI (`gemini`, Google account). CodexBar reads ~/.gemini/oauth_creds.json.";

    public bool UsesApiKey => false;

    public string? StatusLinkUrl => "https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history";

    public string? WorkspaceStatusProductId => "npdyhgECDJ6tB66MxXyo";

    public static string CredentialsPath(ProviderContext context) =>
        Path.Combine(context.HomeDirectory, ".gemini", "oauth_creds.json");

    public bool HasCredentials(ProviderContext context) => File.Exists(CredentialsPath(context));

    public async Task<UsageSnapshot> FetchAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var authType = ReadAuthType(context);
        if (authType is "api-key" or "gemini-api-key" or "vertex-ai")
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                $"Gemini CLI is set to {authType} sign-in, which has no quota to report. Use Google account sign-in instead.");
        }

        string text;
        try
        {
            text = File.ReadAllText(CredentialsPath(context));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Gemini CLI is not signed in on this PC. Run `gemini` and sign in with Google.",
                error);
        }

        var now = context.Clock();
        var credentials = ParseCredentials(text);
        var accessToken = await this.UsableAccessTokenAsync(context, credentials, now, cancellationToken).ConfigureAwait(false);

        var assist = await PostAsync(
            context,
            LoadCodeAssistUrl,
            accessToken,
            """{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}""",
            cancellationToken).ConfigureAwait(false);
        var status = assist.Status == 200 ? ParseCodeAssist(assist.Body, credentials.HostedDomain) : CodeAssistStatus.Unknown;
        if (assist.Status == 401)
        {
            throw new ProviderException(ProviderErrorKind.Unauthorized, "Gemini rejected the sign-in. Run `gemini` and sign in again.");
        }

        // Google answers the consumer shutdown with HTTP 200: no current tier, and the consumer tier listed as
        // ineligible. Paid tiers and Workspace/education domains are outside the shutdown.
        if (status is { ConsumerUnsupported: true, TierId: null, PaidTierName: null } && credentials.HostedDomain is null)
        {
            throw new ProviderException(ProviderErrorKind.Unauthorized, ConsumerShutdownMessage);
        }

        var project = status.ProjectId
            ?? await DiscoverProjectAsync(context, accessToken, cancellationToken).ConfigureAwait(false);
        var quota = await PostAsync(
            context,
            QuotaUrl,
            accessToken,
            project is null ? "{}" : JsonSerializer.Serialize(new Dictionary<string, string> { ["project"] = project }),
            cancellationToken).ConfigureAwait(false);
        switch (quota.Status)
        {
            case 200:
                return ParseUsage(quota.Body, credentials.Email, status.Plan, now);
            case 401:
                throw new ProviderException(ProviderErrorKind.Unauthorized, "Gemini rejected the sign-in. Run `gemini` and sign in again.");
            case 403 when IsShutdownSignal(quota.Body) || (status.ConsumerUnsupported && status.TierId != "standard-tier"):
                throw new ProviderException(ProviderErrorKind.Unauthorized, ConsumerShutdownMessage);
            case 429:
                throw new ProviderException(ProviderErrorKind.RateLimited, "Gemini quota API is rate limited. Try again shortly.");
            default:
                throw new ProviderException(
                    ProviderErrorKind.Server,
                    $"Gemini API error {quota.Status}: {ProviderHttp.Shorten(quota.Body)}");
        }
    }

    public static GeminiCredentials ParseCredentials(string credentialsJson)
    {
        using var document = JsonHelpers.TryParse(credentialsJson)
            ?? throw new ProviderException(ProviderErrorKind.NotConfigured, "Gemini's oauth_creds.json is not valid JSON.");
        JsonElement? root = document.RootElement;
        var accessToken = root.Get("access_token").AsString();
        var refreshToken = root.Get("refresh_token").AsString();
        if (accessToken is null && refreshToken is null)
        {
            throw new ProviderException(
                ProviderErrorKind.NotConfigured,
                "Gemini's oauth_creds.json has no sign-in tokens. Run `gemini` and sign in with Google.");
        }

        DateTimeOffset? expiresAt = null;
        if (root.Get("expiry_date").AsDouble() is { } millis && millis > 0 && millis < 253_402_300_799_000)
        {
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
        }

        string? email = null;
        string? hostedDomain = null;
        using (var claims = JsonHelpers.TryDecodeJwtPayload(root.Get("id_token").AsString()))
        {
            email = claims?.RootElement.Get("email").AsString();
            hostedDomain = claims?.RootElement.Get("hd").AsString();
        }

        return new GeminiCredentials(accessToken, refreshToken, expiresAt, email, hostedDomain);
    }

    /// <summary>Reads the project, tier and plan from a successful <c>loadCodeAssist</c> body.</summary>
    public static CodeAssistStatus ParseCodeAssist(string body, string? hostedDomain)
    {
        using var document = JsonHelpers.TryParse(body);
        if (document is null)
        {
            return CodeAssistStatus.Unknown;
        }

        JsonElement? root = document.RootElement;
        var project = root.Get("cloudaicompanionProject");
        var projectId = project.AsString() ?? project.Get("id", "projectId").AsString();
        var tierId = root.Get("currentTier").Get("id").AsString();
        var paidTier = root.Get("paidTier").Get("name").AsString();
        var unsupported = root.Get("ineligibleTiers").Items().Any(entry =>
            IsShutdownSignal(((JsonElement?)entry).Get("reasonCode").AsString())
            || IsShutdownSignal(((JsonElement?)entry).Get("reasonMessage").AsString()));

        // A named paid tier is the most specific plan signal, even when currentTier is missing or says free.
        var plan = paidTier ?? tierId switch
        {
            "standard-tier" => "Paid",
            "free-tier" => hostedDomain is null ? "Free" : "Workspace",
            "legacy-tier" => "Legacy",
            _ => null,
        };
        return new CodeAssistStatus(projectId, tierId, paidTier, plan, unsupported);
    }

    /// <summary>
    /// Quota buckets are per model; the lowest remaining fraction per model wins, then models are grouped into
    /// Pro, Flash and Flash Lite lanes, each showing its most constrained model.
    /// </summary>
    public static UsageSnapshot ParseUsage(string body, string? email, string? plan, DateTimeOffset now)
    {
        using var document = ProviderHttp.ParseOrThrow(body, "Gemini");
        var perModel = new Dictionary<string, (double Remaining, DateTimeOffset? Reset)>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.Get("buckets").Items())
        {
            JsonElement? bucket = item;
            if (bucket.Get("modelId").AsString() is not { } model || bucket.Get("remainingFraction").AsDouble() is not { } fraction)
            {
                continue;
            }

            model = model.ToLowerInvariant();
            if (!perModel.TryGetValue(model, out var existing) || fraction < existing.Remaining)
            {
                perModel[model] = (fraction, bucket.Get("resetTime").AsIsoDate());
            }
        }

        var windows = new List<RateWindow>();
        AddLane(windows, perModel, "gemini-pro", "Pro models", id => id.Contains("pro", StringComparison.Ordinal));
        AddLane(
            windows,
            perModel,
            "gemini-flash",
            "Flash models",
            id => id.Contains("flash", StringComparison.Ordinal) && !id.Contains("flash-lite", StringComparison.Ordinal));
        AddLane(windows, perModel, "gemini-flash-lite", "Flash Lite models", id => id.Contains("flash-lite", StringComparison.Ordinal));
        return new UsageSnapshot("gemini", windows, [], plan, email, now);
    }

    public static bool IsShutdownSignal(string? text)
    {
        var lower = text?.ToLowerInvariant() ?? string.Empty;
        return lower.Contains("unsupported_client", StringComparison.Ordinal)
            || lower.Contains("ineligibletiererror", StringComparison.Ordinal)
            || (lower.Contains("no longer supported", StringComparison.Ordinal) && lower.Contains("gemini code assist", StringComparison.Ordinal))
            || (lower.Contains("migrate", StringComparison.Ordinal) && lower.Contains("antigravity", StringComparison.Ordinal)
                && lower.Contains("gemini", StringComparison.Ordinal));
    }

    /// <summary>Finds the OAuth client id and secret in the Gemini CLI's own <c>oauth2.js</c> (or bundled chunk) text.</summary>
    public static (string ClientId, string ClientSecret)? ExtractOAuthClient(string javascript)
    {
        var id = ClientIdPattern().Match(javascript);
        var secret = ClientSecretPattern().Match(javascript);
        return id.Success && secret.Success ? (id.Groups[1].Value, secret.Groups[1].Value) : null;
    }

    private static void AddLane(
        List<RateWindow> windows,
        Dictionary<string, (double Remaining, DateTimeOffset? Reset)> perModel,
        string id,
        string title,
        Func<string, bool> matches)
    {
        var lane = perModel.Where(entry => matches(entry.Key)).OrderBy(entry => entry.Value.Remaining).ToList();
        if (lane.Count == 0)
        {
            return;
        }

        var tightest = lane[0].Value;
        windows.Add(new RateWindow(id, title, Math.Clamp(100 - (tightest.Remaining * 100), 0, 100), 1440, tightest.Reset));
    }

    private static string? ReadAuthType(ProviderContext context)
    {
        try
        {
            using var document = JsonHelpers.TryParse(
                File.ReadAllText(Path.Combine(context.HomeDirectory, ".gemini", "settings.json")));
            return document?.RootElement.Get("security").Get("auth").Get("selectedType").AsString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string> UsableAccessTokenAsync(
        ProviderContext context,
        GeminiCredentials credentials,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var margin = TimeSpan.FromSeconds(60);
        if (credentials.AccessToken is { } stored && (credentials.ExpiresAt is not { } expiry || expiry - now > margin))
        {
            return stored;
        }

        if (credentials.RefreshToken is not { } refreshToken)
        {
            throw new ProviderException(
                ProviderErrorKind.Unauthorized,
                "The Gemini CLI sign-in has expired. Run `gemini` once so it renews it, then refresh.");
        }

        lock (this.gate)
        {
            if (this.renewed is { } cached && cached.RefreshToken == refreshToken && cached.ExpiresAt - now > margin)
            {
                return cached.AccessToken;
            }
        }

        var client = ResolveOAuthClient(context)
            ?? throw new ProviderException(
                ProviderErrorKind.Unauthorized,
                "The Gemini CLI sign-in has expired and CodexBar could not find the Gemini CLI install to renew it. "
                + "Run `gemini` once, or set GEMINI_OAUTH_CLIENT_ID and GEMINI_OAUTH_CLIENT_SECRET.");

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }),
        };
        var (status, body) = await ProviderHttp.SendAsync(context.Http, request, this.DisplayName, cancellationToken)
            .ConfigureAwait(false);
        if (status != 200)
        {
            throw new ProviderException(
                ProviderErrorKind.Unauthorized,
                IsShutdownSignal(body) ? ConsumerShutdownMessage : "Gemini sign-in could not be renewed. Run `gemini` and sign in again.");
        }

        using var document = ProviderHttp.ParseOrThrow(body, this.DisplayName);
        JsonElement? root = document.RootElement;
        var accessToken = root.Get("access_token").AsString()
            ?? throw new ProviderException(ProviderErrorKind.InvalidResponse, "Gemini token renewal returned no access token.");
        var lifetime = root.Get("expires_in").AsDouble() ?? 3600;
        lock (this.gate)
        {
            // Memory only: the CLI's credential file is never rewritten.
            this.renewed = (refreshToken, accessToken, now.AddSeconds(lifetime));
        }

        return accessToken;
    }

    /// <summary>Environment override first, then the installed Gemini CLI's own source.</summary>
    private static (string ClientId, string ClientSecret)? ResolveOAuthClient(ProviderContext context)
    {
        var id = context.Environment("GEMINI_OAUTH_CLIENT_ID")?.Trim();
        var secret = context.Environment("GEMINI_OAUTH_CLIENT_SECRET")?.Trim();
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
        {
            return (id, secret);
        }

        var candidates = new List<string>();
        if (context.Environment("GEMINI_OAUTH2_JS_PATH")?.Trim() is { Length: > 0 } configured)
        {
            candidates.Add(configured);
        }

        const string oauthFile = "dist/src/code_assist/oauth2.js";
        foreach (var modules in new[]
                 {
                     Path.Combine(context.AppDataDirectory, "npm", "node_modules"),
                     Path.Combine(context.HomeDirectory, ".bun", "install", "global", "node_modules"),
                 })
        {
            var cli = Path.Combine(modules, "@google", "gemini-cli");
            candidates.Add(Path.Combine(cli, "node_modules", "@google", "gemini-cli-core", oauthFile));
            candidates.Add(Path.Combine(modules, "@google", "gemini-cli-core", oauthFile));
            var bundle = Path.Combine(cli, "bundle");
            if (Directory.Exists(bundle))
            {
                candidates.AddRange(Directory.EnumerateFiles(bundle, "*.js"));
            }
        }

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path) && ExtractOAuthClient(File.ReadAllText(path)) is { } found)
                {
                    return found;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static async Task<string?> DiscoverProjectAsync(
        ProviderContext context,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProjectsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var (status, body) = await ProviderHttp.SendAsync(context.Http, request, "Gemini", cancellationToken).ConfigureAwait(false);
            using var document = status == 200 ? JsonHelpers.TryParse(body) : null;
            foreach (var item in (document?.RootElement.Get("projects")).Items())
            {
                JsonElement? project = item;
                if (project.Get("projectId").AsString() is { } id
                    && (id.StartsWith("gen-lang-client", StringComparison.Ordinal)
                        || project.Get("labels").Get("generative-language") is not null))
                {
                    return id;
                }
            }
        }
        catch (ProviderException)
        {
            // Project discovery is optional; the quota call also works without a project.
        }

        return null;
    }

    private static async Task<(int Status, string Body)> PostAsync(
        ProviderContext context,
        string url,
        string accessToken,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await ProviderHttp.SendAsync(context.Http, request, "Gemini", cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("""OAUTH_CLIENT_ID\s*=\s*['"]([\w\-\.]+)['"]""")]
    private static partial Regex ClientIdPattern();

    [GeneratedRegex("""OAUTH_CLIENT_SECRET\s*=\s*['"]([\w\-]+)['"]""")]
    private static partial Regex ClientSecretPattern();
}

public sealed record GeminiCredentials(
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? Email,
    string? HostedDomain);

public sealed record CodeAssistStatus(string? ProjectId, string? TierId, string? PaidTierName, string? Plan, bool ConsumerUnsupported)
{
    public static CodeAssistStatus Unknown { get; } = new(null, null, null, null, false);
}
