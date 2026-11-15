using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBar.Core;

/// <summary>
/// Estimates token spend from the session logs Codex and Claude Code write on this PC. A reduced port of the
/// upstream cost scanner: every request is priced at API list rates, including the long-context tier, and
/// totalled per day, per model and per project. The figures estimate API value, not what a subscription charges.
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

    /// <summary>USD for one request from the bundled table alone, or null when the model has no known price.</summary>
    public static double? Price(string model, long input, long cachedInput, long cacheWrite, long output) =>
        BundledPrice(model)?.Cost(input, cachedInput, cacheWrite, output);

    /// <summary>The bundled list price for a model, including its long-context tier where one exists.</summary>
    public static ModelPrice? BundledPrice(string model)
    {
        var name = DateSuffix().Replace(Normalize(model), string.Empty);

        // Longest matching prefix wins, so "gpt-5.1-codex-mini" beats "gpt-5.1".
        ModelPrice? best = null;
        var bestLength = 0;
        foreach (var (pattern, price) in Bundled)
        {
            // A trailing '$' means exact match only, so an unknown newer version is left unpriced, not mispriced.
            var exact = pattern.EndsWith('$');
            var prefix = exact ? pattern[..^1] : pattern;
            if (prefix.Length > bestLength
                && (name == prefix || (!exact && name.StartsWith(prefix + "-", StringComparison.Ordinal))))
            {
                best = price;
                bestLength = prefix.Length;
            }
        }

        return best;
    }

    /// <summary>The downloaded catalog knows current models; the bundled table covers older ones and offline use.</summary>
    public ModelPrice? PriceOf(string model)
    {
        var name = Normalize(model);
        var listed = this.catalog?.Find(name) ?? this.catalog?.Find(DateSuffix().Replace(name, string.Empty));
        var bundled = BundledPrice(model);
        if (listed is null)
        {
            return bundled;
        }

        // The catalog sometimes omits a long-context tier the bundled table knows about; keep that tier.
        return listed.LongContext is null && bundled?.LongContext is not null
            ? listed with { LongContextThreshold = bundled.LongContextThreshold, LongContext = bundled.LongContext }
            : listed;
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
        var days = Enumerable.Range(0, WindowDays).Select(offset => new Tally()).ToArray();
        var models = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var projects = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var total = new Tally();

        foreach (var file in EnumerateLogs(roots, oldestWrite))
        {
            foreach (var entry in this.EntriesFor(file, parse))
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.Timestamp, zone).DateTime);
                if (day < firstDay || day > today || (entry.Key is not null && !seen.Add(entry.Key)))
                {
                    continue;
                }

                total.Add(entry);
                days[day.DayNumber - firstDay.DayNumber].Add(entry);
                Bucket(models, entry.Model).Add(entry);
                Bucket(projects, entry.Project ?? "(unknown project)").Add(entry);
            }
        }

        var todayTally = days[^1];
        var report = new CostReport(
            days.Select((tally, index) => new DailyCost(firstDay.AddDays(index), tally.Tokens, tally.Cost)).ToList(),
            Rank(models),
            Rank(projects),
            total.Input,
            total.CacheRead,
            total.CacheWrite,
            total.Output,
            total.Requests,
            total.LongContextRequests);
        return new CostSummary(todayTally.Tokens, todayTally.Cost, total.Tokens, total.Cost, total.Unpriced, now, report);
    }

    private static Tally Bucket(Dictionary<string, Tally> buckets, string name)
    {
        if (!buckets.TryGetValue(name, out var tally))
        {
            buckets[name] = tally = new Tally();
        }

        return tally;
    }

    private static List<CostBreakdown> Rank(Dictionary<string, Tally> buckets) =>
        buckets.Select(entry => new CostBreakdown(entry.Key, entry.Value.Tokens, entry.Value.Cost, entry.Value.Requests, entry.Value.Unpriced))
            .OrderByDescending(row => row.Cost)
            .ThenByDescending(row => row.Tokens)
            .ToList();

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

    private UsageEntry Entry(
        string? key,
        DateTimeOffset timestamp,
        string model,
        string? project,
        long input,
        long cacheRead,
        long cacheWrite,
        long output)
    {
        var price = this.PriceOf(model);
        return new UsageEntry(
            key,
            timestamp,
            string.Intern(DateSuffix().Replace(Normalize(model), string.Empty)),
            project is null ? null : string.Intern(project),
            input,
            cacheRead,
            cacheWrite,
            output,
            price?.Cost(input, cacheRead, cacheWrite, output),
            price?.IsLongContext(input, cacheRead, cacheWrite) == true);
    }

    /// <summary>The folder name of a working directory, which is how a project is labelled.</summary>
    private static string? ProjectName(string? workingDirectory)
    {
        var trimmed = workingDirectory?.Trim().TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length == 0 ? trimmed : name;
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
            if (input + cacheRead + cacheWrite + output == 0)
            {
                continue;
            }

            var messageId = message.Get("id").AsString();
            var requestId = root.Get("requestId", "request_id").AsString();
            var key = messageId is null ? null
                : requestId is not null ? $"{messageId}:{requestId}"
                : root.Get("sessionId", "session_id").AsString() is { } session ? $"{session}:{messageId}"
                : null;
            var entry = this.Entry(key, timestamp, model, ProjectName(root.Get("cwd").AsString()), input, cacheRead, cacheWrite, output);
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
    /// priced with the model named by the latest <c>turn_context</c> and filed under the session's working directory.
    /// </summary>
    private List<UsageEntry> ParseCodexFile(string path)
    {
        var entries = new List<UsageEntry>();
        var model = "gpt-5";
        string? project = null;
        Totals? previous = null;
        var index = 0;
        foreach (var line in ReadLines(path))
        {
            var isContext = line.Contains("\"turn_context\"", StringComparison.Ordinal)
                || line.Contains("\"session_meta\"", StringComparison.Ordinal);
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
            var type = root.Get("type").AsString();
            if (type is "turn_context" or "session_meta")
            {
                model = payload.Get("model").AsString() ?? model;
                project = ProjectName(payload.Get("cwd").AsString()) ?? project;
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
            entries.Add(this.Entry($"{path}#{index++}", timestamp, model, project, delta.Input - cached, cached, 0, delta.Output));
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

    private sealed record UsageEntry(
        string? Key,
        DateTimeOffset Timestamp,
        string Model,
        string? Project,
        long Input,
        long CacheRead,
        long CacheWrite,
        long Output,
        double? Cost,
        bool LongContext)
    {
        public long Tokens => this.Input + this.CacheRead + this.CacheWrite + this.Output;
    }

    private sealed record CachedFile(long Length, DateTime WriteTimeUtc, List<UsageEntry> Entries);

    private sealed class Tally
    {
        public long Input { get; private set; }

        public long CacheRead { get; private set; }

        public long CacheWrite { get; private set; }

        public long Output { get; private set; }

        public double Cost { get; private set; }

        public int Requests { get; private set; }

        public int LongContextRequests { get; private set; }

        public bool Unpriced { get; private set; }

        public long Tokens => this.Input + this.CacheRead + this.CacheWrite + this.Output;

        public void Add(UsageEntry entry)
        {
            this.Input += entry.Input;
            this.CacheRead += entry.CacheRead;
            this.CacheWrite += entry.CacheWrite;
            this.Output += entry.Output;
            this.Cost += entry.Cost ?? 0;
            this.Requests++;
            this.LongContextRequests += entry.LongContext ? 1 : 0;
            this.Unpriced |= entry.Cost is null && entry.Tokens > 0;
        }
    }

    private static ModelPrice Flat(double input, double cached, double write, double output) =>
        new(new ModelRate(input, cached, write, output));

    /// <summary>OpenAI's long-context tier: above 272K prompt tokens the request costs 2x input and 1.5x output.</summary>
    private static ModelPrice Long272(double input, double cached, double output) => new(
        new ModelRate(input, cached, 0, output),
        272_000,
        new ModelRate(input * 2, cached * 2, 0, output * 1.5));

    /// <summary>Claude Sonnet 4 and 4.5: above 200K prompt tokens the request costs 2x input and 1.5x output.</summary>
    private static ModelPrice Sonnet200 { get; } = new(
        new ModelRate(3, 0.3, 3.75, 15),
        200_000,
        new ModelRate(6, 0.6, 7.5, 22.5));

    /// <summary>
    /// List prices in USD per million tokens, taken from the upstream bundled price table
    /// (<c>CostUsagePricing.swift</c>), including its long-context thresholds.
    /// </summary>
    private static readonly (string Prefix, ModelPrice Price)[] Bundled =
    [
        ("gpt-5", Flat(1.25, 0.125, 0, 10)),
        ("gpt-5-mini", Flat(0.25, 0.025, 0, 2)),
        ("gpt-5-nano", Flat(0.05, 0.005, 0, 0.4)),
        ("gpt-5-pro", Flat(15, 15, 0, 120)),
        ("gpt-5.1", Flat(1.25, 0.125, 0, 10)),
        ("gpt-5.1-codex-mini", Flat(0.25, 0.025, 0, 2)),
        ("gpt-5.2", Flat(1.75, 0.175, 0, 14)),
        ("gpt-5.2-pro", Flat(21, 21, 0, 168)),
        ("gpt-5.3-codex", Flat(1.75, 0.175, 0, 14)),
        ("gpt-5.3-codex-spark", Flat(0, 0, 0, 0)),
        ("gpt-5.4", Long272(2.5, 0.25, 15)),
        ("gpt-5.4-mini", Flat(0.75, 0.075, 0, 4.5)),
        ("gpt-5.4-nano", Flat(0.2, 0.02, 0, 1.25)),
        ("gpt-5.4-pro", Long272(30, 30, 180)),
        ("gpt-5.5", Long272(5, 0.5, 30)),
        ("gpt-5.5-pro", Long272(30, 30, 180)),
        ("claude-haiku-4-5", Flat(1, 0.1, 1.25, 5)),
        ("claude-sonnet-4$", Sonnet200),
        ("claude-sonnet-4-5", Sonnet200),
        ("claude-sonnet-4-6", Flat(3, 0.3, 3.75, 15)),
        ("claude-opus-4$", Flat(15, 1.5, 18.75, 75)),
        ("claude-opus-4-1", Flat(15, 1.5, 18.75, 75)),
        ("claude-opus-4-5", Flat(5, 0.5, 6.25, 25)),
        ("claude-opus-4-6", Flat(5, 0.5, 6.25, 25)),
        ("claude-opus-4-7", Flat(5, 0.5, 6.25, 25)),
        ("claude-opus-4-8", Flat(5, 0.5, 6.25, 25)),
    ];
}
