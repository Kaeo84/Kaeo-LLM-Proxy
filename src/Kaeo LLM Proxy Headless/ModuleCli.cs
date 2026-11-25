using System.Reflection;
using Kaeo.LlmProxy.Infrastructure.Modules;

namespace Kaeo.LlmProxy.Headless;

/// <summary>
/// Offline module-packaging verbs for the headless host (no settings/database/proxy startup):
/// <c>--module-keygen</c>, <c>--module-pack</c>, <c>--module-sign</c>. Used by module authors and
/// by the ModuleBuild.targets pack step; works identically on Windows, Linux and macOS.
/// </summary>
internal static class ModuleCli
{
    public static int Run(string[] args)
    {
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "--module-keygen" => Keygen(args[1..]),
                "--module-pack" => Pack(args[1..], sign: false),
                "--module-sign" => Pack(args[1..], sign: true),
                _ => Usage($"Unknown verb '{args[0]}'."),
            };
        }
        catch (Exception ex) when (ex is ModulePackageException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"[module] {ex.Message}");
            return 1;
        }
    }

    private static int Keygen(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
            return Usage("--module-keygen requires an output directory.");
        string outDir = args[0];
        string baseName = GetOption(args, "--name") ?? "kaeo-module";
        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Usage("--name contains invalid filename characters.");

        ModuleSigning.GeneratedKey key = ModuleSigning.GenerateKey();
        Directory.CreateDirectory(outDir);
        string keyPath = Path.Combine(outDir, baseName + ".key");
        string pubPath = Path.Combine(outDir, baseName + ".pub");
        File.WriteAllText(keyPath, key.PrivateKeyPem);
        File.WriteAllText(pubPath, key.PublicKeyPem);

        Console.WriteLine($"[module] Private key: {Path.GetFullPath(keyPath)}  (keep secret; used with --module-sign --key)");
        Console.WriteLine($"[module] Public key:  {Path.GetFullPath(pubPath)}");
        Console.WriteLine($"[module] Key id:      {key.KeyId}");
        Console.WriteLine("[module] Add this entry to AppSettings.TrustedModuleKeys to trust packages signed with this key:");
        Console.WriteLine($"  {{ \"Name\": \"{baseName}\", \"PublicKey\": \"{ModuleSigning.PemToBase64Der(key.PublicKeyPem)}\" }}");
        return 0;
    }

    private static int Pack(string[] args, bool sign)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
            return Usage($"--module-{(sign ? "sign" : "pack")} requires a module directory.");
        string sourceDir = Path.GetFullPath(args[0]);
        string? entry = GetOption(args, "--entry");
        if (string.IsNullOrWhiteSpace(entry))
            return Usage("--entry <assembly> is required.");

        string entryFull = Path.IsPathRooted(entry) ? entry : Path.Combine(sourceDir, entry);
        if (!File.Exists(entryFull))
            return Usage($"The entry assembly '{entryFull}' does not exist.");

        string id = GetOption(args, "--id") ?? Path.GetFileNameWithoutExtension(entryFull);
        string version = GetOption(args, "--version")
            ?? AssemblyName.GetAssemblyName(entryFull).Version?.ToString()
            ?? "1.0.0";
        string outputPath = GetOption(args, "--out")
            ?? Path.Combine(Path.GetDirectoryName(sourceDir) ?? ".", $"{id}.{version}.kaeomod");

        string? privateKeyPem = null;
        if (sign)
        {
            string? keyPath = GetOption(args, "--key");
            if (string.IsNullOrWhiteSpace(keyPath))
                return Usage("--module-sign requires --key <private-key.pem>.");
            if (!File.Exists(keyPath))
                return Usage($"The signing key '{keyPath}' does not exist.");
            privateKeyPem = File.ReadAllText(keyPath);
        }

        ModulePackageWriter.PackageIdentity identity = new(
            id, version, entry,
            Tfm: GetOption(args, "--tfm"),
            Rid: GetOption(args, "--rid"),
            MinHostVersion: GetOption(args, "--min-host-version"));

        string packagePath = ModulePackageWriter.CreatePackage(sourceDir, outputPath, identity, privateKeyPem);
        Console.WriteLine($"[module] {(sign ? "Signed" : "Unsigned")} package: {Path.GetFullPath(packagePath)}");
        Console.WriteLine($"[module] Id: {id}  Version: {version}  Entry: {identity.EntryAssembly}");
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int Usage(string error)
    {
        Console.Error.WriteLine($"[module] {error}");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  --module-keygen <outDir> [--name <base>]");
        Console.Error.WriteLine("  --module-pack <dir> --entry <dll> [--id <id>] [--version <ver>] [--tfm <tfm>] [--rid <rid>] [--min-host-version <ver>] [--out <file>]");
        Console.Error.WriteLine("  --module-sign <dir> --entry <dll> --key <private.pem> [same options as --module-pack]");
        return 1;
    }
}