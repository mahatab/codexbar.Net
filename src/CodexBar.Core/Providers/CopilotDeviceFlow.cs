namespace CodexBar.Core.Providers;

public sealed record DeviceCode(string Code, string UserCode, string VerificationUrl, int ExpiresInSeconds, int IntervalSeconds);

/// <summary>GitHub OAuth device flow for Copilot, mirroring <c>CopilotDeviceFlow.swift</c>.</summary>
public sealed class CopilotDeviceFlow
{
    // VS Code's public client id, the same one the macOS app uses.
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string Scope = "read:user";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public CopilotDeviceFlow(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.http = http;
        this.delay = delay ?? Task.Delay;
    }

    public async Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var body = await this.PostFormAsync(
            DeviceCodeUrl,
            new() { ["client_id"] = ClientId, ["scope"] = Scope },
            cancellationToken).ConfigureAwait(false);
        using var document = ProviderHttp.ParseOrThrow(body, "GitHub");
        var root = document.RootElement;
        var deviceCode = root.Get("device_code").AsString();
        var userCode = root.Get("user_code").AsString();
        var url = root.Get("verification_uri_complete").AsString() ?? root.Get("verification_uri").AsString();
        if (deviceCode is null || userCode is null || url is null)
        {
            throw new ProviderException(ProviderErrorKind.InvalidResponse, "GitHub did not return a sign-in code.");
        }

        return new DeviceCode(
            deviceCode,
            userCode,
            url,
            (int)(root.Get("expires_in").AsDouble() ?? 900),
            (int)(root.Get("interval").AsDouble() ?? 5));
    }

    /// <summary>Polls until the user approves the code in their browser, then returns the GitHub token.</summary>
    public async Task<string> PollForTokenAsync(DeviceCode code, CancellationToken cancellationToken)
    {
        var interval = Math.Max(1, code.IntervalSeconds);
        var remaining = Math.Max(0, code.ExpiresInSeconds);
        while (remaining > 0)
        {
            await this.delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            remaining -= interval;

            var body = await this.PostFormAsync(
                AccessTokenUrl,
                new()
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = code.Code,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                },
                cancellationToken).ConfigureAwait(false);
            using var document = ProviderHttp.ParseOrThrow(body, "GitHub");
            var root = document.RootElement;
            switch (root.Get("error").AsString())
            {
                case null:
                    break;
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "access_denied":
                    throw new ProviderException(ProviderErrorKind.Unauthorized, "GitHub sign-in was cancelled.");
                case "expired_token":
                    throw new ProviderException(ProviderErrorKind.Unauthorized, "The GitHub sign-in code expired. Try again.");
                default:
                    throw new ProviderException(ProviderErrorKind.Unauthorized, "GitHub sign-in failed.");
            }

            return root.Get("access_token").AsString()
                ?? throw new ProviderException(ProviderErrorKind.InvalidResponse, "GitHub did not return a token.");
        }

        throw new ProviderException(ProviderErrorKind.Unauthorized, "The GitHub sign-in code expired. Try again.");
    }

    private async Task<string> PostFormAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var (status, body) = await ProviderHttp.SendAsync(this.http, request, "GitHub", cancellationToken)
            .ConfigureAwait(false);
        if (status != 200)
        {
            throw new ProviderException(ProviderErrorKind.Server, $"GitHub sign-in error {status}.");
        }

        return body;
    }
}
