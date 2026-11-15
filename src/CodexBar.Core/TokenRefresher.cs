using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexBar.Core;

/// <summary>
/// Opt-in renewal of expired Codex and Claude Code sign-in tokens.
///
/// Renewing rotates the refresh token, so the new tokens must be written back to the CLI's own credential file
/// or the CLI is signed out. That is why this is off by default: the provider only calls it when the user has
/// switched on "Renew expired sign-ins" in Settings. Every other field in the file is preserved, the write is
/// atomic, and nothing is written if the file changed while the request was in flight.
/// </summary>
public static class TokenRefresher
{
    private const string CodexTokenUrl = "https://auth.openai.com/oauth/token";
    private const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string ClaudeTokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClaudeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Renews the Codex tokens in <c>auth.json</c>. Returns false when there is nothing to renew with.</summary>
    public static Task<bool> RefreshCodexAsync(ProviderContext context, string authPath, CancellationToken cancellationToken) =>
        RefreshAsync(
            context,
            authPath,
            "Codex",
            root => root["tokens"]?["refresh_token"]?.GetValue<string>(),
            refreshToken => new HttpRequestMessage(HttpMethod.Post, CodexTokenUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["client_id"] = CodexClientId,
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken,
                        ["scope"] = "openid profile email",
                    }),
                    Encoding.UTF8,
                    "application/json"),
            },
            (root, response, now) =>
            {
                var tokens = root["tokens"]!.AsObject();
                foreach (var name in new[] { "access_token", "refresh_token", "id_token" })
                {
                    if (response[name]?.GetValue<string>() is { Length: > 0 } value)
                    {
                        tokens[name] = value;
                    }
                }

                root["last_refresh"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            },
            cancellationToken);

    /// <summary>Renews the Claude Code tokens in <c>.credentials.json</c>.</summary>
    public static Task<bool> RefreshClaudeAsync(ProviderContext context, string credentialsPath, CancellationToken cancellationToken) =>
        RefreshAsync(
            context,
            credentialsPath,
            "Claude",
            root => root["claudeAiOauth"]?["refreshToken"]?.GetValue<string>(),
            refreshToken => new HttpRequestMessage(HttpMethod.Post, ClaudeTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = ClaudeClientId,
                }),
            },
            (root, response, now) =>
            {
                var oauth = root["claudeAiOauth"]!.AsObject();
                oauth["accessToken"] = response["access_token"]!.GetValue<string>();
                if (response["refresh_token"]?.GetValue<string>() is { Length: > 0 } rotated)
                {
                    oauth["refreshToken"] = rotated;
                }

                var lifetime = response["expires_in"]?.GetValue<double>() ?? 3600;
                oauth["expiresAt"] = now.AddSeconds(lifetime).ToUnixTimeMilliseconds();
            },
            cancellationToken);

    private static async Task<bool> RefreshAsync(
        ProviderContext context,
        string path,
        string providerName,
        Func<JsonNode, string?> readRefreshToken,
        Func<string, HttpRequestMessage> buildRequest,
        Action<JsonNode, JsonNode, DateTimeOffset> apply,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = File.ReadAllText(path);
            var refreshToken = ReadToken(original, readRefreshToken);
            if (refreshToken is null)
            {
                return false;
            }

            using var request = buildRequest(refreshToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            var (status, body) = await ProviderHttp.SendAsync(context.Http, request, providerName, cancellationToken)
                .ConfigureAwait(false);
            if (status != 200)
            {
                throw new ProviderException(
                    status is 400 or 401 or 403 ? ProviderErrorKind.Unauthorized : ProviderErrorKind.Server,
                    $"{providerName} would not renew the sign-in (HTTP {status}). Sign in again with the {providerName} CLI.");
            }

            JsonNode response;
            try
            {
                response = JsonNode.Parse(body) ?? throw new JsonException("empty");
                if (response["access_token"]?.GetValue<string>() is not { Length: > 0 })
                {
                    throw new JsonException("no access token");
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                throw new ProviderException(
                    ProviderErrorKind.InvalidResponse,
                    $"{providerName} returned a token response CodexBar.Net could not read.",
                    error);
            }

            // The CLI may have renewed in the meantime. Its tokens are then the live ones; ours must not replace them.
            var current = File.ReadAllText(path);
            if (ReadToken(current, readRefreshToken) != refreshToken)
            {
                return true;
            }

            var root = JsonNode.Parse(current)!;
            apply(root, response, context.Clock());
            var temporary = path + ".codexbar.tmp";
            File.WriteAllText(temporary, root.ToJsonString(Indented));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderErrorKind.Unauthorized,
                $"Could not update the {providerName} credential file: {error.Message}",
                error);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? ReadToken(string json, Func<JsonNode, string?> read)
    {
        try
        {
            return JsonNode.Parse(json) is { } root && read(root) is { Length: > 0 } token ? token : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
