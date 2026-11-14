using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>
/// Estimates token spend from the session logs Codex and Claude Code write on this PC. A reduced port of the
/// upstream cost scanner: it prices tokens at standard API list rates (no long-context or fast-mode tiers),
/// so the figures are estimates of API value, not what a subscription actually charges.
/// </summary>
public sealed partial class CostScanner
{
    public const int WindowDays = 30;

    private readonly object gate = new();
    private readonly Dictionary<string, CachedFile> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly PriceCatalog? catalog;
    private int pricedWithVersion;

    /// <param name="catalog">Current list prices; without it only the bundled table is used.</param>
    public CostScanner(PriceCatalog? catalog = null) => this.catalog = catalog;

    public PriceCatalog? Catalog => this.catalog;

    public CostSummary ScanClaude(string claudeRoot, DateTimeOffset now, TimeZoneInfo? zone = null) =>
        this.Scan(
            [Path.Combine(claudeRoot, "projects")],
            now,
            zone ?? TimeZoneInfo.Local,
            this.ParseClaudeFile);

    public CostSummary ScanCodex(string codexRoot, DateTimeOffset now, TimeZoneInfo? zone = null) =>
        this.Scan(
            [Path.Combine(codexRoot, "sessions"), Path.Combine(codexRoot, "archived_sessions")],
            now,
            zone ?? TimeZoneInfo.Local,
            this.ParseCodexFile);

    /// <summary>USD for one usage record at list price, or null when the model has no known price.</summary>
    public static double? Price(string model, long input, long cachedInput, long cacheWrite, long output)
    {
        if (FindRate(model) is not { } rate)
        {
            return null;
        }

        return ((input * rate.Input) + (cachedInput * rate.CachedInput) + (cacheWrite * rate.CacheWrite)
            + (output * rate.Output)) / 1_000_000;
    }

    /// <summary>The downloaded catalog knows current models; the bundled table covers older ones and offline use.</summary>
    private double? PriceFor(string model, long input, long cachedInput, long cacheWrite, long output)
    {
        var name = Normalize(model);
        if ((this.catalog?.Find(name) ?? this.catalog?.Find(DateSuffix().Replace(name, string.Empty))) is { } rate)
        {
            return ((input * rate.Input) + (cachedInput * rate.CachedInput) + (cacheWrite * rate.CacheWrite)
                + (output * rate.Output)) / 1_000_000;
        }

        return Price(model, input, cachedInput, cacheWrite, output);
    }

    private static string Normalize(string model)
    {
        var name = model.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "openai/", "anthropic/", "anthropic." })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
            }
        }

        return name;
    }

    private static Rate? FindRate(string model)
    {
        var name = DateSuffix().Replace(Normalize(model), string.Empty);

        // Longest matching prefix wins, so "gpt-5.1-codex-mini" beats "gpt-5.1".
        Rate? best = null;
        var bestLength = 0;
        foreach (var (pattern, rate) in Rates)
        {
            // A trailing '$' means exact match only, so an unknown newer version is left unpriced, not mispriced.
            var exact = pattern.EndsWith('$');
            var prefix = exact ? pattern[..^1] : pattern;
            if (prefix.Length > bestLength
                && (name == prefix || (!exact && name.StartsWith(prefix + "-", StringComparison.Ordinal))))
            {
                best = rate;
                bestLength = prefix.Length;
            }
        }

        return best;
    }

    private CostSummary Scan(
        IReadOnlyList<string> roots,
        DateTimeOffset now,
        TimeZoneInfo zone,
        Func<string, List<UsageEntry>> parse)
    {
        lock (this.gate)
        {
            // Parsed entries carry a computed cost; new prices mean they must be parsed again.
            if (this.catalog is { } prices && prices.Version != this.pricedWithVersion)
            {
                this.cache.Clear();
                this.pricedWithVersion = prices.Version;
            }
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var firstDay = today.AddDays(-(WindowDays - 1));
        var oldestWrite = now.UtcDateTime.AddDays(-(WindowDays + 1));

        // The same response can be copied into a resumed session's file; the key keeps it counted once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long todayTokens = 0, monthTokens = 0;
        double todayCost = 0, monthCost = 0;
        var unpriced = false;

        foreach (var file in EnumerateLogs(roots, oldestWrite))
        {
            foreach (var entry in this.EntriesFor(file, parse))
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.Timestamp, zone).DateTime);
                if (day < firstDay || day > today || (entry.Key is not null && !seen.Add(entry.Key)))
                {
                    continue;
                }

                monthTokens += entry.Tokens;
                monthCost += entry.Cost ?? 0;
                unpriced |= entry.Cost is null && entry.Tokens > 0;
                if (day == today)
                {
                    todayTokens += entry.Tokens;
                    todayCost += entry.Cost ?? 0;
                }
            }
        }

        return new CostSummary(todayTokens, todayCost, monthTokens, monthCost, unpriced, now);
    }

    private static IEnumerable<FileInfo> EnumerateLogs(IReadOnlyList<string> roots, DateTime oldestWriteUtc)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.jsonl", options))
            {
                if (file.LastWriteTimeUtc >= oldestWriteUtc)
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>Re-parses a log only when its size or modified time changed since the last scan.</summary>
    private List<UsageEntry> EntriesFor(FileInfo file, Func<string, List<UsageEntry>> parse)
    {
        lock (this.gate)
        {
            if (this.cache.TryGetValue(file.FullName, out var cached)
                && cached.Length == file.Length && cached.WriteTimeUtc == file.LastWriteTimeUtc)
            {
                return cached.Entries;
            }
        }

        List<UsageEntry> entries;
        try
        {
            entries = parse(file.FullName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        lock (this.gate)
        {
            this.cache[file.FullName] = new CachedFile(file.Length, file.LastWriteTimeUtc, entries);
        }

        return entries;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        // The CLI may be appending to this file right now; share it for reading and writing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>Claude Code: assistant lines carry <c>message.usage</c>; streaming repeats a message, last one wins.</summary>
    private List<UsageEntry> ParseClaudeFile(string path)
    {
        var byKey = new Dictionary<string, UsageEntry>(StringComparer.Ordinal);
        var unkeyed = new List<UsageEntry>();
        foreach (var line in ReadLines(path))
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal) || !line.Contains("\"assistant\"", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonHelpers.TryParse(line);
            if (document is null || document.RootElement.Get("type").AsString() != "assistant")
            {
                continue;
            }

            var root = document.RootElement;
            var message = root.Get("message");
            var usage = message.Get("usage");
            var model = message.Get("model").AsString();
            if (usage is null || model is null || model.StartsWith('<') || root.Get("timestamp").AsIsoDate() is not { } timestamp)
            {
                continue;
            }

            var input = Count(usage.Get("input_tokens"));
            var cacheRead = Count(usage.Get("cache_read_input_tokens"));
            var cacheWrite = Count(usage.Get("cache_creation_input_tokens"));
            var output = Count(usage.Get("output_tokens"));
            var tokens = input + cacheRead + cacheWrite + output;
            if (tokens == 0)
            {
                continue;
            }

            var messageId = message.Get("id").AsString();
            var requestId = root.Get("requestId", "request_id").AsString();
            var key = messageId is null ? null
                : requestId is not null ? $"{messageId}:{requestId}"
                : root.Get("sessionId", "session_id").AsString() is { } session ? $"{session}:{messageId}"
                : null;
            var entry = new UsageEntry(key, timestamp, tokens, this.PriceFor(model, input, cacheRead, cacheWrite, output));
            if (key is null)
            {
                unkeyed.Add(entry);
            }
            else
            {
                byKey[key] = entry;
            }
        }

        return [.. byKey.Values, .. unkeyed];
    }

    /// <summary>
    /// Codex: <c>token_count</c> events carry cumulative totals; each entry is the growth since the previous event,
    /// priced with the model named by the latest <c>turn_context</c>.
    /// </summary>
    private List<UsageEntry> ParseCodexFile(string path)
    {
        var entries = new List<UsageEntry>();
        var model = "gpt-5";
        Totals? previous = null;
        var index = 0;
        foreach (var line in ReadLines(path))
        {
            var isContext = line.Contains("\"turn_context\"", StringComparison.Ordinal);
            if (!isContext && !line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonHelpers.TryParse(line);
            if (document is null)
            {
                continue;
            }

            var root = document.RootElement;
            var payload = root.Get("payload");
            if (root.Get("type").AsString() == "turn_context")
            {
                model = payload.Get("model").AsString() ?? model;
                continue;
            }

            if (payload.Get("type").AsString() != "token_count" || root.Get("timestamp").AsIsoDate() is not { } timestamp)
            {
                continue;
            }

            var info = payload.Get("info");
            model = info.Get("model").AsString() ?? payload.Get("model").AsString() ?? model;
            Totals delta;
            if (ReadTotals(info.Get("total_token_usage")) is { } total)
            {
                // A total that went backwards means a new context; count it from zero.
                delta = previous is { } before && total.Input >= before.Input && total.Output >= before.Output
                    ? new Totals(total.Input - before.Input, Math.Max(0, total.Cached - before.Cached), total.Output - before.Output)
                    : total;
                previous = total;
            }
            else if (ReadTotals(info.Get("last_token_usage")) is { } last)
            {
                delta = last;
            }
            else
            {
                continue;
            }

            if (delta.Input + delta.Output == 0)
            {
                continue;
            }

            // Codex counts cached tokens inside input_tokens.
            var cached = Math.Min(delta.Cached, delta.Input);
            entries.Add(new UsageEntry(
                $"{path}#{index++}",
                timestamp,
                delta.Input + delta.Output,
                this.PriceFor(model, delta.Input - cached, cached, 0, delta.Output)));
        }

        return entries;
    }

    private static Totals? ReadTotals(JsonElement? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new Totals(
            Count(usage.Get("input_tokens")),
            Math.Max(Count(usage.Get("cached_input_tokens")), Count(usage.Get("cache_read_input_tokens"))),
            Count(usage.Get("output_tokens")));
    }

    private static long Count(JsonElement? value) =>
        value.AsDouble() is { } number && number > 0 && number < 1e15 ? (long)number : 0;

    [GeneratedRegex(@"(-\d{8}|-\d{4}-\d{2}-\d{2}|-v\d+:\d+)$")]
    private static partial Regex DateSuffix();

    private readonly record struct Totals(long Input, long Cached, long Output);

    private sealed record UsageEntry(string? Key, DateTimeOffset Timestamp, long Tokens, double? Cost);

    private sealed record CachedFile(long Length, DateTime WriteTimeUtc, List<UsageEntry> Entries);

    /// <summary>USD per million tokens.</summary>
    private readonly record struct Rate(double Input, double CachedInput, double CacheWrite, double Output);

    /// <summary>Standard list rates, taken from the upstream bundled price table (<c>CostUsagePricing.swift</c>).</summary>
    private static readonly (string Prefix, Rate Rate)[] Rates =
    [
        ("gpt-5", new Rate(1.25, 0.125, 0, 10)),
        ("gpt-5-mini", new Rate(0.25, 0.025, 0, 2)),
        ("gpt-5-nano", new Rate(0.05, 0.005, 0, 0.4)),
        ("gpt-5-pro", new Rate(15, 15, 0, 120)),
        ("gpt-5.1", new Rate(1.25, 0.125, 0, 10)),
        ("gpt-5.1-codex-mini", new Rate(0.25, 0.025, 0, 2)),
        ("gpt-5.2", new Rate(1.75, 0.175, 0, 14)),
        ("gpt-5.2-pro", new Rate(21, 21, 0, 168)),
        ("gpt-5.3-codex", new Rate(1.75, 0.175, 0, 14)),
        ("gpt-5.3-codex-spark", new Rate(0, 0, 0, 0)),
        ("gpt-5.4", new Rate(2.5, 0.25, 0, 15)),
        ("gpt-5.4-mini", new Rate(0.75, 0.075, 0, 4.5)),
        ("gpt-5.4-nano", new Rate(0.2, 0.02, 0, 1.25)),
        ("gpt-5.4-pro", new Rate(30, 30, 0, 180)),
        ("gpt-5.5", new Rate(5, 0.5, 0, 30)),
        ("gpt-5.5-pro", new Rate(30, 30, 0, 180)),
        ("claude-haiku-4-5", new Rate(1, 0.1, 1.25, 5)),
        ("claude-sonnet-4$", new Rate(3, 0.3, 3.75, 15)),
        ("claude-sonnet-4-5", new Rate(3, 0.3, 3.75, 15)),
        ("claude-sonnet-4-6", new Rate(3, 0.3, 3.75, 15)),
        ("claude-opus-4$", new Rate(15, 1.5, 18.75, 75)),
        ("claude-opus-4-1", new Rate(15, 1.5, 18.75, 75)),
        ("claude-opus-4-5", new Rate(5, 0.5, 6.25, 25)),
        ("claude-opus-4-6", new Rate(5, 0.5, 6.25, 25)),
        ("claude-opus-4-7", new Rate(5, 0.5, 6.25, 25)),
        ("claude-opus-4-8", new Rate(5, 0.5, 6.25, 25)),
    ];
}
