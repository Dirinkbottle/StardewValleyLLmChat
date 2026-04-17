using System.Text.Json;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private static int ReadInt(JsonElement element, string propertyName, int fallback)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out int parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool ReadBool(JsonElement element, string propertyName, bool fallback)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out bool parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool TryReadRequiredBool(JsonElement element, string propertyName, out bool value)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement property))
        {
            value = false;
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out bool parsed))
        {
            value = parsed;
            return true;
        }

        value = false;
        return false;
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback = "")
    {
        if (!TryGetProperty(element, propertyName, out JsonElement property))
        {
            return fallback;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static int[] ReadIntArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        List<int> values = new();
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number))
            {
                values.Add(number);
                continue;
            }

            if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out int parsed))
            {
                values.Add(parsed);
            }
        }

        return values.ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (JsonProperty candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}
