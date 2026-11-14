using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBar.Core;

/// <summary>USD per million tokens for one model.</summary>
public readonly record struct ModelRate(double Input, double CachedInput, double CacheWrite, double Output);

/// <summary>
/// Current model list prices from the public models.dev catalog, the same source the macOS app uses for models
/// newer than its bundled table. Only Anthropic and OpenAI prices are kept, in a small local cache refreshed
/// about once a day. The request carries no account or usage data.
/// </summary>
public sealed class PriceCatalog
{
    public const string CatalogUrl = "https://models.dev/api.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly string? path;
    private readonly object gate = new();
    private Dictionary<string, ModelRate> rates = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;

    /// <param name="path">Cache file, or null to keep the catalog in memory only.</param>
    public PriceCatalog(string? path)
    {
        this.path = path;
        this.LoadCache();
    }

    /// <summary>Changes whenever the price list changes, so cached cost figures can be recomputed.</summary>
    public int Version { get; private set; }

    public ModelRate? Find(string model)
    {
        lock (this.gate)
        {
            return this.rates.TryGetValue(model, out var rate) ? rate : null;
        }
    }

    /// <summary>Downloads the catalog when the cache is older than a day. Never throws: prices are optional.</summary>
    public async Task EnsureFreshAsync(HttpClient http, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            if (now - this.fetchedAt < MaxAge || now - this.lastAttempt < RetryAfterFailure)
            {
                return;
            }

            this.lastAttempt = now;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var body = await http.GetStringAsync(CatalogUrl, timeout.Token).ConfigureAwait(false);
            var parsed = Parse(body);
            if (parsed.Count == 0)
            {
                return;
            }

            lock (this.gate)
            {
                this.rates = parsed;
                this.fetchedAt = now;
                this.Version++;
            }

            this.SaveCache(parsed, now);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or UnauthorizedAccessException
                                          || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Keep whatever prices are already known.
        }
    }

    /// <summary>Extracts <c>cost</c> blocks for the Anthropic and OpenAI providers from the models.dev document.</summary>
    public static Dictionary<string, ModelRate> Parse(string body)
    {
        var result = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonHelpers.TryParse(body);
        if (document is null)
        {
            return result;
        }

        foreach (var provider in new[] { "anthropic", "openai" })
        {
            var models = document.RootElement.Get(provider).Get("models");
            if (models is not { ValueKind: JsonValueKind.Object } list)
            {
                continue;
            }

            foreach (var model in list.EnumerateObject())
            {
                JsonElement? cost = ((JsonElement?)model.Value).Get("cost");
                if (cost.Get("input").AsDouble() is not { } input || cost.Get("output").AsDouble() is not { } output)
                {
                    continue;
                }

                // No published cache rate means cached tokens are billed as ordinary input.
                var cached = cost.Get("cache_read").AsDouble() ?? input;
                result[model.Name] = new ModelRate(input, cached, cost.Get("cache_write").AsDouble() ?? input, output);
            }
        }

        return result;
    }

    private void LoadCache()
    {
        if (this.path is null || !File.Exists(this.path))
        {
            return;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(this.path));
            if (cache?.Rates is { Count: > 0 } saved)
            {
                this.rates = saved.Where(entry => entry.Value.Length == 4).ToDictionary(
                    entry => entry.Key,
                    entry => new ModelRate(entry.Value[0], entry.Value[1], entry.Value[2], entry.Value[3]),
                    StringComparer.OrdinalIgnoreCase);
                this.fetchedAt = DateTimeOffset.FromUnixTimeSeconds(cache.FetchedAt);
                this.Version++;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // A damaged cache is simply downloaded again.
        }
    }

    private void SaveCache(Dictionary<string, ModelRate> parsed, DateTimeOffset now)
    {
        if (this.path is null)
        {
            return;
        }

        var cache = new CacheFile
        {
            FetchedAt = now.ToUnixTimeSeconds(),
            Rates = parsed.ToDictionary(
                entry => entry.Key,
                entry => new[] { entry.Value.Input, entry.Value.CachedInput, entry.Value.CacheWrite, entry.Value.Output }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        var temporary = this.path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(cache));
        File.Move(temporary, this.path, overwrite: true);
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("fetchedAt")]
        public long FetchedAt { get; set; }

        [JsonPropertyName("rates")]
        public Dictionary<string, double[]> Rates { get; set; } = [];
    }
}
