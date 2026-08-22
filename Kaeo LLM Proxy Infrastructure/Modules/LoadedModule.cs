using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// A module assembly that was successfully loaded and initialized, together with the load
/// context that owns it (kept so the assembly can be unloaded on disable/remove).
/// </summary>
internal sealed class LoadedModule
{
    public required ModuleRegistryEntry Entry { get; init; }

    public required IKaeoModule Module { get; init; }

    public required ModuleAssemblyLoadContext LoadContext { get; init; }
}
