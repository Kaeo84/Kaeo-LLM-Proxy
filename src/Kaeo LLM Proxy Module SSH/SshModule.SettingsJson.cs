using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.Ssh;

/// <summary>
/// JSON settings surface for the web dashboard, over the same persisted feature settings the
/// WinForms config page edits (<c>mcp_ssh_settings</c>). Stored connections remain managed in
/// the WinForms UI.
/// </summary>
public sealed partial class SshModule : IModuleSettingsJson
{
    string IModuleSettingsJson.GetSettingsJson() =>
        ModuleSettingsJson.Serialize(Repository.LoadSettings());

    void IModuleSettingsJson.ApplySettingsJson(string json)
    {
        SshSettings settings = ModuleSettingsJson.Deserialize<SshSettings>(json);

        ModuleSettingsJson.RequireRange(settings.DefaultIdleTimeoutSeconds, 0, 86_400, nameof(settings.DefaultIdleTimeoutSeconds));
        ModuleSettingsJson.RequireRange(settings.CommandTimeoutSeconds, 5, 3_600, nameof(settings.CommandTimeoutSeconds));
        ModuleSettingsJson.RequireRange(settings.MaxOutputChars, 1_000, 200_000, nameof(settings.MaxOutputChars));
        ModuleSettingsJson.RequireDefinedEnum(settings.McpLogLevel, nameof(settings.McpLogLevel));
        ModuleSettingsJson.RequireDefinedEnum(settings.CommandPolicy, nameof(settings.CommandPolicy));
        settings.AllowedCommands ??= SshCommandGuard.DefaultAllowedCommands;

        Repository.SaveSettings(settings);
    }
}