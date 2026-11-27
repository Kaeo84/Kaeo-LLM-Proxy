using Kaeo.LlmProxy.Core.Security;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Utilities;

/// <summary>
/// Round-trip and failure-mode coverage for the reversible envelope that lets an administrator
/// reveal a stored proxy API key. The protector must never throw: a missing or replaced key file
/// has to degrade to "not recoverable" so the caller can offer regeneration instead.
/// </summary>
public sealed class ApiKeyProtectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kaeo-keyprot-{Guid.NewGuid():N}");
    private readonly string _keyFile;
    private readonly string _databasePath;

    public ApiKeyProtectorTests()
    {
        Directory.CreateDirectory(_directory);
        _keyFile = Path.Combine(_directory, "api-key-protection.key");
        _databasePath = Path.Combine(_directory, "kaeo.db");
    }

    [Fact]
    public void TryEncryptThenTryDecrypt_RoundTripsThePlaintext()
    {
        ApiKeyProtector protector = new(_keyFile);
        const string plainText = "kaeo-0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.True(protector.TryEncrypt(plainText, out string? envelope));
        Assert.NotNull(envelope);
        Assert.True(ApiKeyProtector.IsProtected(envelope));
        Assert.DoesNotContain(plainText, envelope);

        Assert.True(protector.TryDecrypt(envelope, out string? recovered));
        Assert.Equal(plainText, recovered);
    }

    [Fact]
    public void TryEncrypt_UsesAFreshNonceForTheSamePlaintext()
    {
        ApiKeyProtector protector = new(_keyFile);
        const string plainText = "kaeo-abcdef";

        Assert.True(protector.TryEncrypt(plainText, out string? first));
        Assert.True(protector.TryEncrypt(plainText, out string? second));

        Assert.NotEqual(first, second);
        Assert.True(protector.TryDecrypt(first, out string? firstRecovered));
        Assert.True(protector.TryDecrypt(second, out string? secondRecovered));
        Assert.Equal(firstRecovered, secondRecovered);
    }

    [Fact]
    public void KeyFile_IsReusedAcrossInstances_SoStoredKeysStayReadable()
    {
        Assert.True(new ApiKeyProtector(_keyFile).TryEncrypt("kaeo-persisted", out string? envelope));

        // A restart (new process, new instance) must read the same key file back.
        Assert.True(new ApiKeyProtector(_keyFile).TryDecrypt(envelope, out string? recovered));
        Assert.Equal("kaeo-persisted", recovered);
    }

    [Fact]
    public void TryDecrypt_RejectsEnvelopesFromADifferentKeyFile()
    {
        Assert.True(new ApiKeyProtector(_keyFile).TryEncrypt("kaeo-original", out string? envelope));

        string otherKeyFile = Path.Combine(_directory, "other-protection.key");
        ApiKeyProtector other = new(otherKeyFile);
        Assert.True(other.TryEncrypt("kaeo-other", out _));

        Assert.False(other.TryDecrypt(envelope, out string? recovered));
        Assert.Null(recovered);
    }

    [Fact]
    public void TryDecrypt_ReturnsFalse_ForMissingFileMalformedOrPlaintextValues()
    {
        ApiKeyProtector protector = new(_keyFile);

        Assert.False(protector.TryDecrypt(null, out _));
        Assert.False(protector.TryDecrypt(string.Empty, out _));
        Assert.False(protector.TryDecrypt("kaeo-not-an-envelope", out _));
        Assert.False(protector.TryDecrypt("kaeo-key:v1:not-base64!!", out _));
        Assert.False(protector.TryDecrypt("kaeo-key:v1:" + Convert.ToBase64String([1, 2, 3]), out _));

        // Decrypt must not create a key file as a side effect; encrypt is what bootstraps it.
        Assert.False(File.Exists(_keyFile));
    }

    [Fact]
    public void TryEncrypt_ReturnsFalse_ForEmptyPlaintext()
    {
        ApiKeyProtector protector = new(_keyFile);

        Assert.False(protector.TryEncrypt(null, out string? envelope));
        Assert.Null(envelope);
        Assert.False(protector.TryEncrypt(string.Empty, out _));
    }

    [Fact]
    public void ForDatabase_PlacesTheKeyFileBesideTheDatabase()
    {
        ApiKeyProtector protector = ApiKeyProtector.ForDatabase(_databasePath);

        Assert.Equal(_directory, Path.GetDirectoryName(protector.KeyFilePath));
        Assert.EndsWith("api-key-protection.key", protector.KeyFilePath, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A sharing violation from a parallel test run is not worth failing over.
        }
    }
}