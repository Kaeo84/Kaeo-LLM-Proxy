using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.WebSearch;

/// <summary>
/// JSON settings surface for the web dashboard, over the same persisted feature settings the
/// WinForms config page edits. The provider catalog and domain rules remain managed in the
/// WinForms UI; this contract covers the <c>mcp_web_search_settings</c> feature toggles/limits.
/// </summary>
public sealed partial class WebSearchModule : IModuleSettingsJson
{
    string IModuleSettingsJson.GetSettingsJson() =>
        ModuleSettingsJson.Serialize(Repository.LoadWebSearchSettings());

    void IModuleSettingsJson.ApplySettingsJson(string json)
    {
        WebSearchSettings settings = ModuleSettingsJson.Deserialize<WebSearchSettings>(json);

        ModuleSettingsJson.RequireRange(settings.MaxResults, 1, 20, nameof(settings.MaxResults));
        ModuleSettingsJson.RequireRange(settings.TimeoutSeconds, 5, 120, nameof(settings.TimeoutSeconds));
        ModuleSettingsJson.RequireRange(settings.MaxResponseBytes, 10_000, 2_000_000, nameof(settings.MaxResponseBytes));

        Repository.SaveWebSearchSettings(settings);
    }
}