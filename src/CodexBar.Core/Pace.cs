namespace CodexBar.Core;

/// <summary>
/// Whether a quota window will run out before it resets, judged from usage so far against time elapsed.
/// </summary>
public sealed record PaceReading(double ExpectedPercent, double ProjectedPercent, DateTimeOffset? RunsOutAt)
{
    public bool WillRunOut => this.RunsOutAt is not null;
}

public static class Pace
{
    /// <summary>Too early in a window, a few requests look like a wild burn rate; wait for some signal.</summary>
    private const double MinimumElapsedFraction = 0.05;

    public static PaceReading? Compute(RateWindow window, DateTimeOffset now)
    {
        if (window.WindowMinutes is not { } minutes || minutes <= 0 || window.ResetsAt is not { } resetsAt)
        {
            return null;
        }

        var length = TimeSpan.FromMinutes(minutes);
        var remaining = resetsAt - now;
        if (remaining <= TimeSpan.Zero || remaining > length)
        {
            return null;
        }

        var elapsed = length - remaining;
        var fraction = elapsed / length;
        if (fraction < MinimumElapsedFraction)
        {
            return null;
        }

        var used = Math.Clamp(window.UsedPercent, 0, 100);
        var projected = used / fraction;
        DateTimeOffset? runsOutAt = null;
        if (used >= 100)
        {
            runsOutAt = now;
        }
        else if (projected > 100 && used > 0)
        {
            // Same burn rate: the remaining percent lasts (remaining% / used%) of the time already elapsed.
            runsOutAt = now + (elapsed * ((100 - used) / used));
        }

        return new PaceReading(fraction * 100, projected, runsOutAt);
    }

    /// <summary>One short phrase for the card, or null when there is nothing useful to say.</summary>
    public static string? Describe(RateWindow window, DateTimeOffset now)
    {
        if (Compute(window, now) is not { } pace)
        {
            return null;
        }

        if (window.UsedPercent >= 100)
        {
            return "Limit reached";
        }

        if (pace.RunsOutAt is { } runsOutAt)
        {
            return $"On pace to run out in {Span(runsOutAt - now)}";
        }

        var margin = pace.ExpectedPercent - window.UsedPercent;
        return margin >= 5 ? $"{Math.Round(margin):0}% under pace" : "On pace";
    }

    private static string Span(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
        {
            return "under 1m";
        }

        if (span.TotalHours < 1)
        {
            return $"{span.Minutes}m";
        }

        return span.TotalDays < 1 ? $"{span.Hours}h {span.Minutes}m" : $"{(int)span.TotalDays}d {span.Hours}h";
    }
}
