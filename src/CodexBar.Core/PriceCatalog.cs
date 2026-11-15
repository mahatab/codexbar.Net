using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBar.Core;

/// <summary>USD per million tokens for one model.</summary>
public readonly record struct ModelRate(double Input, double CachedInput, double CacheWrite, double Output);

/// <summary>
/// A model's list price. Some models charge more for a long prompt: when a request's whole prompt (uncached input
/// plus cache reads plus cache writes) exceeds <see cref="LongContextThreshold"/>, the <see cref="LongContext"/>
/// rates apply to the entire request.
/// </summary>
public sealed record ModelPrice(ModelRate Standard, long? LongContextThreshold = null, ModelRate? LongContext = null)
{
    public bool IsLongContext(long input, long cachedInput, long cacheWrite) =>
        this.LongContextThreshold is { } threshold && this.LongContext is not null
        && input + cachedInput + cacheWrite > threshold;

    /// <summary>USD for one request.</summary>
    public double Cost(long input, long cachedInput, long cacheWrite, long output)
    {
        var rate = this.IsLongContext(input, cachedInput, cacheWrite) ? this.LongContext!.Value : this.Standard;
        return ((input * rate.Input) + (cachedInput * rate.CachedInput) + (cacheWrite * rate.CacheWrite)
            + (output * rate.Output)) / 1_000_000;
    }
}

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
    private Dictionary<string, ModelPrice> prices = new(StringComparer.OrdinalIgnoreCase);
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

    public ModelPrice? Find(string model)
    {
        lock (this.gate)
        {
            return this.prices.GetValueOrDefault(model);
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
                this.prices = parsed;
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
    public static Dictionary<string, ModelPrice> Parse(string body)
    {
        var result = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
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
                if (ReadRate(cost, null) is not { } standard)
                {
                    continue;
                }

                // A context tier lists the rates for prompts above its size; the older field implies 200K.
                long? threshold = null;
                ModelRate? above = null;
                foreach (var item in cost.Get("tiers").Items())
                {
                    JsonElement? tier = item;
                    if (tier.Get("tier").Get("type").AsString() == "context"
                        && tier.Get("tier").Get("size").AsDouble() is { } size && size > 0
                        && ReadRate(tier, standard) is { } tierRate)
                    {
                        threshold = (long)size;
                        above = tierRate;
                        break;
                    }
                }

                if (above is null && ReadRate(cost.Get("context_over_200k"), standard) is { } over200)
                {
                    threshold = 200_000;
                    above = over200;
                }

                result[model.Name] = new ModelPrice(standard, threshold, above);
            }
        }

        return result;
    }

    /// <summary>Missing cache rates fall back to the standard ones, or to the input rate when there are none.</summary>
    private static ModelRate? ReadRate(JsonElement? cost, ModelRate? fallback)
    {
        if (cost.Get("input").AsDouble() is not { } input || cost.Get("output").AsDouble() is not { } output)
        {
            return null;
        }

        // No published cache rate means cached tokens are billed as ordinary input.
        var scale = fallback is { Input: > 0 } standard ? input / standard.Input : 1;
        var cached = cost.Get("cache_read").AsDouble() ?? (fallback is { } f ? f.CachedInput * scale : input);
        var write = cost.Get("cache_write").AsDouble() ?? (fallback is { } g ? g.CacheWrite * scale : input);
        return new ModelRate(input, cached, write, output);
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
                // Four numbers are the standard rates; nine add the long-context threshold and its four rates.
                this.prices = saved.Where(entry => entry.Value.Length is 4 or 9).ToDictionary(
                    entry => entry.Key,
                    entry =>
                    {
                        var v = entry.Value;
                        var standard = new ModelRate(v[0], v[1], v[2], v[3]);
                        return v.Length == 9
                            ? new ModelPrice(standard, (long)v[4], new ModelRate(v[5], v[6], v[7], v[8]))
                            : new ModelPrice(standard);
                    },
                    StringComparer.OrdinalIgnoreCase);
                // A cache written before long-context tiers existed is used for now but refreshed right away.
                this.fetchedAt = cache.Format >= 2 ? DateTimeOffset.FromUnixTimeSeconds(cache.FetchedAt) : DateTimeOffset.MinValue;
                this.Version++;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // A damaged cache is simply downloaded again.
        }
    }

    private void SaveCache(Dictionary<string, ModelPrice> parsed, DateTimeOffset now)
    {
        if (this.path is null)
        {
            return;
        }

        var cache = new CacheFile
        {
            // Version 2 added long-context tiers; an older cache has no version and is refreshed.
            Format = 2,
            FetchedAt = now.ToUnixTimeSeconds(),
            Rates = parsed.ToDictionary(
                entry => entry.Key,
                entry =>
                {
                    var s = entry.Value.Standard;
                    return entry.Value is { LongContextThreshold: { } threshold, LongContext: { } a }
                        ? new[] { s.Input, s.CachedInput, s.CacheWrite, s.Output, threshold, a.Input, a.CachedInput, a.CacheWrite, a.Output }
                        : new[] { s.Input, s.CachedInput, s.CacheWrite, s.Output };
                }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        var temporary = this.path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(cache));
        File.Move(temporary, this.path, overwrite: true);
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("format")]
        public int Format { get; set; }

        [JsonPropertyName("fetchedAt")]
        public long FetchedAt { get; set; }

        [JsonPropertyName("rates")]
        public Dictionary<string, double[]> Rates { get; set; } = [];
    }
}
