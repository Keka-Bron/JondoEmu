using System.Text.Json;

namespace Jondo.Unity.Server.Mods;

internal sealed record ServerModConfiguration(string Id, string? Name, string? ActivationSetting,
    ServerModSetting[] Fields)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static ServerModConfiguration? Read(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("settings", out var metadata)) return null;
        try
        {
            var schema = metadata.Deserialize<ServerModConfiguration>(Json);
            if (schema?.Fields == null || schema.Fields.Any(field => field == null || string.IsNullOrWhiteSpace(field.Key)))
                return null;
            return schema with { Id = manifest.GetProperty("id").GetString()! };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonElement Resolve(ServerSettings settings, string id, ServerModConfiguration? schema)
        => JsonSerializer.SerializeToElement(Values(settings, id, schema));

    internal static bool IsActive(ServerModConfiguration? schema, JsonElement configuration) =>
        schema?.ActivationSetting is not { } activation ||
        !configuration.TryGetProperty(activation, out var enabled) || enabled.ValueKind != JsonValueKind.False;

    internal static void Set<T>(ServerSettings settings, ServerModConfiguration schema, string key, T value)
    {
        var values = Values(settings, schema.Id, schema);
        values[key] = JsonSerializer.SerializeToElement(value);
        settings.ModSettings[schema.Id] = JsonSerializer.SerializeToElement(values);
    }

    private static Dictionary<string, JsonElement> Values(ServerSettings settings, string id, ServerModConfiguration? schema)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        bool hasSection = settings.ModSettings.TryGetValue(id, out var existing) && existing.ValueKind == JsonValueKind.Object;
        if (hasSection)
            foreach (var property in existing.EnumerateObject())
                values[property.Name] = property.Value;

        if (schema == null) return values;
        foreach (var field in schema.Fields)
        {
            var value = field.Default;
            if (values.Remove(field.Key, out var configured) && MatchesFieldType(configured, value))
                value = configured;
            else if (!hasSection && field.LegacyKey is { } legacy
                && settings.ModSettings.TryGetValue(legacy, out var migrated) && MatchesFieldType(migrated, value))
                value = migrated;
            if (value.ValueKind is not JsonValueKind.Undefined) values[field.Key] = value;
        }
        return values;
    }

    private static bool MatchesFieldType(JsonElement value, JsonElement defaultValue)
        => defaultValue.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            JsonValueKind.Number when defaultValue.TryGetInt32(out _) =>
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            _ => value.ValueKind == defaultValue.ValueKind,
        };
}

internal sealed record ServerModSetting(string Key, JsonElement Default, LocalizedModText? Label, LocalizedModText? Hint,
    string? LegacyKey, int Minimum, int Maximum, int Step, string? Unit);

internal sealed record LocalizedModText(string? Es, string? En, string? Fr);
