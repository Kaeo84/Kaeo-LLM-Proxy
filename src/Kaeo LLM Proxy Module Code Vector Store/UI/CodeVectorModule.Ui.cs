using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.CodeVector;

/// <summary>
/// Windows-only UI partial of <see cref="CodeVectorModule"/>. Implements <see cref="IModuleUi"/>
/// with WinForms pages; compiled only for the Windows TFM (the UI/ folder is excluded from the
/// portable net10.0 build so the module loads headless on Linux/macOS without this part).
/// </summary>
public sealed partial class CodeVectorModule : IModuleUi
{
    public object CreateConfigPage() => new CodeVectorConfigPage(this);

    public object? CreateHelpPage() => CodeVectorHelpPage.Create();
}