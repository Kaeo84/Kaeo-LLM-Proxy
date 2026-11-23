using Kaeo.LlmProxy.Module.Ssh;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Tests for the SSH server-side command guardrail: Guarded mode must be fail-closed against
/// chaining / injection / unlisted executables, while FullAccess is an explicit unrestricted opt-in.
/// </summary>
public class SshCommandGuardTests
{
    private static SshSettings Guarded() => new() { CommandPolicy = SshCommandPolicy.Guarded };
    private static SshSettings Full() => new() { CommandPolicy = SshCommandPolicy.FullAccess };

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("ls; rm -rf /")]
    [InlineData("ls && rm -rf /")]
    [InlineData("ls || rm")]
    [InlineData("cat file | sh")]
    [InlineData("echo $(rm -rf /)")]
    [InlineData("echo `rm -rf /`")]
    [InlineData("ls > out.txt")]
    [InlineData("ls < in.txt")]
    [InlineData("ls\nrm -rf /")]
    [InlineData("sudo ls")]
    [InlineData("bash -c 'rm -rf /'")]
    [InlineData("FOO=bar ls")]
    [InlineData("/tmp/ls")]
    [InlineData("/home/user/ls")]
    public void Guarded_Blocks_Dangerous_Or_Unlisted(string command)
    {
        Assert.False(SshCommandGuard.TryEvaluate(command, Guarded(), out string? reason));
        Assert.NotNull(reason);
        Assert.Contains("guardrail", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("ls -la /var")]
    [InlineData("pwd")]
    [InlineData("cat /etc/hostname")]
    [InlineData("/bin/ls")]
    [InlineData("/usr/bin/cat /etc/hostname")]
    [InlineData("grep \"foo bar\" file.txt")]
    public void Guarded_Allows_Simple_Readonly(string command)
    {
        Assert.True(SshCommandGuard.TryEvaluate(command, Guarded(), out string? reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("ls; rm -rf /")]
    [InlineData("sudo anything")]
    public void FullAccess_Allows_Anything(string command)
    {
        Assert.True(SshCommandGuard.TryEvaluate(command, Full(), out _));
    }

    [Fact]
    public void Guarded_Honors_Custom_Allowlist()
    {
        var settings = new SshSettings { CommandPolicy = SshCommandPolicy.Guarded, AllowedCommands = "mycmd\nother" };
        Assert.True(SshCommandGuard.TryEvaluate("mycmd arg", settings, out _));
        Assert.False(SshCommandGuard.TryEvaluate("ls", settings, out _));
    }

    [Fact]
    public void Guarded_Blank_Allowlist_Falls_Back_To_Default()
    {
        var settings = new SshSettings { CommandPolicy = SshCommandPolicy.Guarded, AllowedCommands = "   " };
        Assert.True(SshCommandGuard.TryEvaluate("ls", settings, out _));
    }

    [Fact]
    public void Defaults_Are_Guarded_And_Credentials_Bound()
    {
        var settings = new SshSettings();
        Assert.Equal(SshCommandPolicy.Guarded, settings.CommandPolicy);
        Assert.True(settings.BindCredentialsToStoredHosts);
    }
}
