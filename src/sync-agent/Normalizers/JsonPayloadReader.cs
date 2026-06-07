using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SyncAgent.Normalizers;

internal static class JsonPayloadReader
{
    public static void AddIfPresent(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value;
        }
    }

    public static void AddIfPresent(JsonObject target, string name, bool? value)
    {
        if (value is not null)
        {
            target[name] = value.Value;
        }
    }

    public static void AddIfPresent(JsonObject target, string name, decimal? value)
    {
        if (value is not null)
        {
            target[name] = value.Value;
        }
    }

    public static void AddIfPresent(JsonObject target, string name, JsonArray? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    public static string? ReadFirstString(JsonObject source, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(source, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static string? ReadString(JsonObject source, string name)
    {
        if (!source.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>()?.Trim(),
            JsonValueKind.Number => node.ToJsonString().Trim('"'),
            _ => node.ToJsonString().Trim('"')
        };
    }

    public static bool? ReadBoolean(JsonObject source, string name)
    {
        if (!source.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(node.GetValue<string>(), out var parsed) => parsed,
            JsonValueKind.Number when int.TryParse(node.ToJsonString(), out var number) => number != 0,
            _ => null
        };
    }

    public static decimal? ReadDecimal(JsonObject source, string name)
    {
        var value = ReadString(source, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static DateTimeOffset? ReadDateTimeOffset(JsonObject source, params string[] names)
    {
        var value = ReadFirstString(source, names);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    public static JsonArray? ReadArray(JsonObject source, string name)
    {
        if (!source.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node as JsonArray;
    }

    public static string? OnlyDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }
}
