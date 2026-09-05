using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Kaeo.LlmProxy.VSExtension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid("d1f6a6c2-0000-4a6d-9c2f-000000000000")]
public sealed class VsPackage : AsyncPackage
{
    protected override System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // Initialization logic here (tool window registration, commands)
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
