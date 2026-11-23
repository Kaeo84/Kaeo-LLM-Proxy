namespace Kaeo.LlmProxy.Module.Ssh;

/// <summary>How much freedom <c>ssh_exec</c> has over remote command content.</summary>
internal enum SshCommandPolicy
{
    /// <summary>Default: only allow-listed read-only commands, and no shell metacharacters.</summary>
    Guarded = 0,

    /// <summary>Explicit opt-in: any command is permitted; the owner accepts full responsibility.</summary>
    FullAccess = 1,
}

/// <summary>
/// Server-side command guardrail for <c>ssh_exec</c>, enforced in the tool execution path (not the
/// UI) so it cannot be bypassed through alternate parameters. Guarded mode is fail-closed: it
/// rejects any shell metacharacter (chaining, pipes, redirection, substitution, newlines) and only
/// permits allow-listed read-only executables, so encoding / chaining tricks cannot smuggle a
/// write or an exec past it. <see cref="SshCommandPolicy.FullAccess"/> is a persisted, explicit
/// opt-in for owners who want unrestricted access.
/// </summary>
internal static class SshCommandGuard
{
    /// <summary>Read-only diagnostics permitted by default in Guarded mode (one per line).</summary>
    public const string DefaultAllowedCommands =
        "ls\npwd\nwhoami\nid\nuname\nhostname\nuptime\ndate\ndf\ndu\nfree\nps\ncat\nhead\ntail\n" +
        "wc\nstat\nfile\nwhich\nwhereis\necho\ngrep\nfind\nlsblk\nlscpu\nnetstat\nss\nvmstat\niostat\nlast\nw\nusers\ngroups";

    // Characters that enable chaining, pipes, redirection, command substitution, or multi-line
    // smuggling. Any occurrence blocks the command in Guarded mode.
    private const string Metacharacters = ";&|`$()<>\n\r";

    // Absolute paths are only honoured for these standard system binary directories, so an
    // attacker-writable location (e.g. /tmp/ls) cannot impersonate an allow-listed command.
    private static readonly string[] SystemBinPrefixes = ["/bin/", "/usr/bin/", "/sbin/", "/usr/sbin/"];

    /// <summary>
    /// Returns true when the command may run under the configured policy; otherwise false with a
    /// user-presentable <paramref name="blockReason"/> explaining the guardrail and how to relax it.
    /// </summary>
    public static bool TryEvaluate(string command, SshSettings settings, out string? blockReason)
    {
        blockReason = null;
        if (settings.CommandPolicy == SshCommandPolicy.FullAccess)
            return true;

        if (command.IndexOfAny(Metacharacters.ToCharArray()) >= 0)
        {
            blockReason =
                "Blocked by the SSH command guardrail (Guarded mode): shell metacharacters " +
                "(; & | ` $ ( ) < > or newlines) are not permitted because they enable chaining, " +
                "pipes, redirection or command substitution. Run a single simple command, add it to " +
                "the allowed list, or switch the SSH module to Full access in settings if you accept the risk.";
            return false;
        }

        string[] tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string executable = tokens[0];
        HashSet<string> allowed = ParseAllowed(settings.AllowedCommands);

        bool permitted;
        if (executable.Contains('/'))
        {
            string baseName = executable[(executable.LastIndexOf('/') + 1)..];
            permitted = allowed.Contains(baseName) &&
                        SystemBinPrefixes.Any(p => executable.StartsWith(p, StringComparison.Ordinal));
        }
        else
        {
            permitted = allowed.Contains(executable);
        }

        if (!permitted)
        {
            blockReason =
                $"Blocked by the SSH command guardrail (Guarded mode): '{executable}' is not in the " +
                $"allowed read-only command list. Allowed: {string.Join(", ", allowed.OrderBy(n => n, StringComparer.Ordinal))}. " +
                "Add it in the SSH module settings, or switch to Full access if you accept the risk.";
            return false;
        }

        return true;
    }

    private static HashSet<string> ParseAllowed(string? configured)
    {
        string text = string.IsNullOrWhiteSpace(configured) ? DefaultAllowedCommands : configured;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in text.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = entry.Trim();
            if (name.Length > 0)
                set.Add(name);
        }

        return set;
    }
}
