using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Shared JSON conventions and validation helpers for <see cref="IModuleSettingsJson"/>
/// implementations: camelCase, case-insensitive reads, string enums (integer values also
/// accepted), indented output so the dashboard's raw-JSON editor stays readable.
/// </summary>
public static class ModuleSettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    /// <summary>Serializes a module settings object with the shared conventions.</summary>
    public static string Serialize<T>(T settings) => JsonSerializer.Serialize(settings, Options);

    /// <summary>
    /// Deserializes a full settings object. Missing properties take the type's defaults, so
    /// callers should round-trip the object returned by <see cref="IModuleSettingsJson.GetSettingsJson"/>
    /// rather than sending partial patches. Throws <see cref="JsonException"/> on malformed JSON
    /// and <see cref="ArgumentException"/> on a null/non-object payload.
    /// </summary>
    public static T Deserialize<T>(string json) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new ArgumentException("Settings JSON must be a non-null object.");
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="value"/> is outside [min, max].</summary>
    public static void RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentException($"{name} must be between {min} and {max}.");
    }

    /// <summary>Throws <see cref="ArgumentException"/> when the enum value is not a defined member.</summary>
    public static void RequireDefinedEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentException($"{name} is not a valid value.");
    }
}