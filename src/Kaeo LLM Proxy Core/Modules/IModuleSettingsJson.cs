namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Optional contract for modules that expose their configuration as JSON so a non-WinForms host
/// (the web dashboard) can render and edit it. Modules that only offer a WinForms config page
/// (<see cref="IModuleUi"/>) need not implement this; the dashboard reports "not supported" for
/// those. Implementations read/write the module's own persisted settings (typically via
/// <see cref="IModuleDatabase"/>); <see cref="ApplySettingsJson"/> must validate input and persist
/// atomically, throwing on invalid JSON or values.
/// </summary>
public interface IModuleSettingsJson
{
    /// <summary>Returns the module's current settings serialized as a JSON object.</summary>
    string GetSettingsJson();

    /// <summary>
    /// Validates and applies a JSON settings object, persisting it. Throws
    /// <see cref="System.Text.Json.JsonException"/> on malformed JSON or
    /// <see cref="ArgumentException"/> on invalid values; the caller surfaces the message.
    /// </summary>
    void ApplySettingsJson(string json);
}