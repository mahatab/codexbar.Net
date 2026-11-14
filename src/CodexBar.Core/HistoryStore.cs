using System.Text.Json;

namespace CodexBar.Core;

public readonly record struct HistorySample(long UnixSeconds, double UsedPercent);

/// <summary>
/// Keeps a week of usage readings per quota window so the popup can draw a small trend line.
/// Stored as plain JSON next to the config; it holds percentages and timestamps only.
/// </summary>
public sealed class HistoryStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromMinutes(4);

    private readonly string? path;
    private readonly object gate = new();
    private readonly Dictionary<string, List<HistorySample>> series;

    /// <param name="path">File to persist to, or null to keep history in memory only.</param>
    public HistoryStore(string? path)
    {
        this.path = path;
        this.series = Load(path);
    }

    public static string Key(string providerId, string windowId) => $"{providerId}/{windowId}";

    /// <summary>Adds the snapshot's windows, keeping at most one reading per few-minute bucket.</summary>
    public void Record(UsageSnapshot snapshot)
    {
        var now = snapshot.UpdatedAt.ToUnixTimeSeconds();
        var cutoff = now - (long)Retention.TotalSeconds;
        lock (this.gate)
        {
            foreach (var window in snapshot.Windows)
            {
                var key = Key(snapshot.ProviderId, window.Id);
                if (!this.series.TryGetValue(key, out var samples))
                {
                    this.series[key] = samples = [];
                }

                var sample = new HistorySample(now, Math.Round(Math.Clamp(window.UsedPercent, 0, 100), 1));
                var bucket = (long)MinimumSpacing.TotalSeconds;
                if (samples.Count > 0 && samples[^1].UnixSeconds / bucket == now / bucket)
                {
                    // Keep one reading per few-minute bucket: the newest replaces the earlier one in the same bucket.
                    samples[^1] = sample;
                }
                else
                {
                    samples.Add(sample);
                }

                samples.RemoveAll(entry => entry.UnixSeconds < cutoff);
            }

            this.Save();
        }
    }

    public IReadOnlyList<HistorySample> Samples(string providerId, string windowId)
    {
        lock (this.gate)
        {
            return this.series.TryGetValue(Key(providerId, windowId), out var samples) ? [.. samples] : [];
        }
    }

    private static Dictionary<string, List<HistorySample>> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<double[]>>>(File.ReadAllText(path)) ?? [];
            return raw.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Where(pair => pair.Length == 2)
                    .Select(pair => new HistorySample((long)pair[0], pair[1]))
                    .ToList());
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // History is disposable: start over rather than fail the app.
            return [];
        }
    }

    private void Save()
    {
        if (this.path is null)
        {
            return;
        }

        try
        {
            var raw = this.series.Where(entry => entry.Value.Count > 0).ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Select(sample => new[] { sample.UnixSeconds, sample.UsedPercent }).ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
            var temporary = this.path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(raw));
            File.Move(temporary, this.path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing a history write only costs a point on the trend line.
        }
    }
}
