using Kaeo.LlmProxy.Core.Models;
using Microsoft.AspNetCore.Identity;

namespace Kaeo.LlmProxy.Web;

internal static class DashboardPasswords
{
    private static readonly PasswordHasher<ProxyUser> Hasher = new();

    public static string Hash(ProxyUser user, string password)
        => Hasher.HashPassword(user, password);

    public static bool Verify(ProxyUser user, string password)
        => !string.IsNullOrEmpty(user.DashboardPasswordHash)
            && Hasher.VerifyHashedPassword(user, user.DashboardPasswordHash, password) != PasswordVerificationResult.Failed;
}
