using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// JSON settings surface for the web dashboard, over the same persisted feature settings the
/// WinForms config page edits (<c>mcp_chat_settings</c>). Channel configuration remains managed
/// in the WinForms UI.
/// </summary>
public sealed partial class ChatInterfaceModule : IModuleSettingsJson
{
    string IModuleSettingsJson.GetSettingsJson() =>
        ModuleSettingsJson.Serialize(Repository.LoadSettings());

    void IModuleSettingsJson.ApplySettingsJson(string json)
    {
        ChatSettings settings = ModuleSettingsJson.Deserialize<ChatSettings>(json);

        ModuleSettingsJson.RequireRange(settings.DefaultAskTimeoutSeconds, 10, 3_600, nameof(settings.DefaultAskTimeoutSeconds));
        ModuleSettingsJson.RequireRange(settings.MaxMessageChars, 500, 50_000, nameof(settings.MaxMessageChars));
        ModuleSettingsJson.RequireRange(settings.DefaultPollIntervalSeconds, 5, 300, nameof(settings.DefaultPollIntervalSeconds));
        ModuleSettingsJson.RequireDefinedEnum(settings.McpLogLevel, nameof(settings.McpLogLevel));

        Repository.SaveSettings(settings);
    }
}