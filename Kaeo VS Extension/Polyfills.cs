// Compiler shims for the net48 target. .NET 5+ provides these in the BCL, so each is
// guarded to compile only on net48 (and earlier). Without IsExternalInit the `init`
// accessors used in this project (e.g. ChatLine.Kind) fail to compile on the
// Framework target.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker type the C# compiler requires to enable `init` accessors on
    /// .NET Framework targets. Must be internal to the compiling assembly.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
#endif
