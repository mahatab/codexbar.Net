using System.Text.Json;

namespace CodexBar.Core;

internal static class ProviderHttp
{
    /// <summary>Sends a request and returns the status and body, mapping transport failures to <see cref="ProviderException"/>.</summary>
    public static async Task<(int Status, string Body)> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        string providerName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, body);
        }
        catch (HttpRequestException error)
        {
            throw new ProviderException(
                ProviderErrorKind.Network,
                $"Could not reach {providerName}: {error.Message}",
                error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorKind.Network, $"{providerName} request timed out.", error);
        }
    }

    public static JsonDocument ParseOrThrow(string body, string providerName) =>
        JsonHelpers.TryParse(body)
        ?? throw new ProviderException(
            ProviderErrorKind.InvalidResponse,
            $"{providerName} returned a response CodexBar could not read.");

    public static string Shorten(string body)
    {
        var cleaned = body.ReplaceLineEndings(" ").Trim();
        return cleaned.Length > 200 ? cleaned[..200] + "…" : cleaned;
    }
}
