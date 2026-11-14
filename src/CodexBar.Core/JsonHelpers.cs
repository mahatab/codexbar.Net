using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexBar.Core;

/// <summary>Lenient JSON accessors. Provider payloads drift, so a wrong type reads as "absent" rather than throwing.</summary>
internal static class JsonHelpers
{
    public static JsonElement? Get(this JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return value;
            }
        }

        return null;
    }

    public static JsonElement? Get(this JsonElement? element, params string[] names) =>
        element is { } value ? value.Get(names) : null;

    public static string? AsString(this JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.String } value)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Reads a JSON number, or a numeric string. Booleans and anything non-finite are ignored.</summary>
    public static double? AsDouble(this JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        double? number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
        return number is { } finite && double.IsFinite(finite) ? finite : null;
    }

    public static bool? AsBool(this JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static DateTimeOffset? AsIsoDate(this JsonElement? element)
    {
        var text = element.AsString();
        return text is not null && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    public static DateTimeOffset? AsUnixSeconds(this JsonElement? element)
    {
        var seconds = element.AsDouble();
        if (seconds is not { } value || value <= 0 || value > 253_402_300_799)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds((long)value);
    }

    public static IEnumerable<JsonElement> Items(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Decodes the claims of a JWT without verifying it; used only to show the signed-in email and plan.</summary>
    public static JsonDocument? TryDecodeJwtPayload(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 })
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        try
        {
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }
}
