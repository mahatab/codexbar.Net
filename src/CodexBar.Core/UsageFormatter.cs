using System.Globalization;

namespace CodexBar.Core;

public static class UsageFormatter
{
    public static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>Names a quota lane from its length, for example 300 minutes becomes "Session (5h)".</summary>
    public static string WindowTitle(int? minutes) => minutes switch
    {
        null => "Usage",
        300 => "Session (5h)",
        10080 => "Weekly",
        >= 40320 and <= 44640 => "Monthly",
        < 60 => $"{minutes} min",
        < 1440 when minutes % 60 == 0 => $"{minutes / 60}h window",
        _ when minutes % 1440 == 0 => $"{minutes / 1440}-day window",
        _ => $"{minutes / 60}h window",
    };

    /// <summary>Countdown such as "Resets in 2h 14m" or "Resets in 3d 4h".</summary>
    public static string? ResetCountdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset)
        {
            return null;
        }

        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Resetting now";
        }

        if (remaining.TotalMinutes < 1)
        {
            return "Resets in under 1m";
        }

        if (remaining.TotalHours < 1)
        {
            return $"Resets in {remaining.Minutes}m";
        }

        return remaining.TotalDays < 1
            ? $"Resets in {remaining.Hours}h {remaining.Minutes}m"
            : $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
    }

    public static string Percent(double value) => $"{Math.Round(value, MidpointRounding.AwayFromZero):0}%";

    public static string Money(double amount, string currencyCode)
    {
        var text = amount.ToString("N2", CultureInfo.CurrentCulture);
        return currencyCode.ToUpperInvariant() switch
        {
            "USD" => "$" + text,
            "EUR" => "€" + text,
            "GBP" => "£" + text,
            _ => $"{text} {currencyCode.ToUpperInvariant()}",
        };
    }

    public static string UpdatedAgo(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        var age = now - updatedAt;
        if (age.TotalSeconds < 60)
        {
            return "Updated just now";
        }

        return age.TotalHours < 1
            ? $"Updated {(int)age.TotalMinutes}m ago"
            : $"Updated {(int)age.TotalHours}h ago";
    }
}
