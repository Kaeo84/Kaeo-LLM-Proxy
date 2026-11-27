# Module Packaging and Signing

Kaeo LLM Proxy ships its optional features (Code Vector Store, SSH, Web Search, Chat Interface)
as **module packages** — single `.kaeomod` files that can be imported from either GUI or built and
signed offline with the headless host. This document covers the package format, the CLI, the
signature trust list, and the three load-policy modes.

## What a module is

A module is a class library that implements `Kaeo.LlmProxy.Core.Modules.IKaeoModule` and is
compiled against the host's `Kaeo LLM Proxy Core` assembly. A module may additionally implement:

| Interface | Gives the module |
|-----------|------------------|
| `IMcpToolModule` | MCP tools exposed by the MCP server (visible to MCP clients and the VS extension) |
| `IRunnableModule` | `StartAsync`/`StopAsync` so the host can start and stop a long-running service |

Modules run in a collectible `AssemblyLoadContext` with a late-bound database gateway and secret
provider, so a module never references the host's implementation assemblies directly.

## Package format

A `.kaeomod` file is a ZIP archive with this layout:

```
manifest.json          package identity + SHA-256 of every payload file
manifest.sig           ECDSA P-256 signature over the exact manifest.json bytes (signed packages only)
<module payload...>    the module DLL, its dependencies, and any content files
```

`manifest.json` fields (camelCase, indented, nulls omitted):

| Field | Meaning |
|-------|---------|
| `id` | Package identity; also the folder name under `Modules/{id}/` |
| `version` | Package version; the second folder name under `Modules/{id}/{version}/` |
| `entryAssembly` | Zip-style (`/`) relative path of the DLL containing the `IKaeoModule` implementation |
| `tfm` | Target framework the payload was built for (informational) |
| `rid` | Runtime identifier, when the payload is platform-specific (informational) |
| `minHostVersion` | Minimum host assembly version required to load the package |
| `files` | `{ "relative/path": "<sha256 lowercase hex>" }` for every payload file |

Verification is end-to-end: the signature covers the manifest bytes, and the manifest covers every
payload file. Tampering with any file, adding a stowaway file, dropping a file, or editing the
manifest invalidates the package. The same checks run again when the extracted package is loaded,
so post-install tampering is caught too.

## Building a package

The verbs live in the headless host and work identically on Windows, Linux, and macOS. They run
before settings, database, or proxy startup, so no configuration is needed.

```bash
# 1. Generate a signing key pair (once). Writes <base>.key (private) and <base>.pub (public).
./Kaeo\ LLM\ Proxy\ Headless --module-keygen keys --name kaeo-build

# 2. Pack a published module directory. --entry is the DLL that implements IKaeoModule.
./Kaeo\ LLM\ Proxy\ Headless --module-pack "artifacts/CodeVector" \
  --entry "Kaeo LLM Proxy Module Code Vector Store.dll" \
  --id kaeo.codevector --version 1.0.0 --out artifacts/kaeo.codevector.kaeomod

# 3. Or pack and sign in one step.
./Kaeo\ LLM\ Proxy\ Headless --module-sign "artifacts/CodeVector" \
  --entry "Kaeo LLM Proxy Module Code Vector Store.dll" \
  --key keys/kaeo-build.key --id kaeo.codevector --version 1.0.0 \
  --out artifacts/kaeo.codevector.kaeomod
```

Options for `--module-pack` / `--module-sign`:

| Option | Default | Meaning |
|--------|---------|---------|
| `--entry <dll>` | required | Entry assembly, relative to the module directory |
| `--id <id>` | entry file name without extension | Package identity |
| `--version <ver>` | entry assembly version (`1.0.0` if unset) | Package version |
| `--tfm <tfm>` | — | Target framework recorded in the manifest |
| `--rid <rid>` | — | Runtime identifier recorded in the manifest |
| `--min-host-version <ver>` | — | Refuse to load on older hosts |
| `--key <private.pem>` | required for `--module-sign` | Private key written by `--module-keygen` |
| `--out <file>` | `<id>.<version>.kaeomod` next to the module directory | Output package |

Keep `.key` private. `.pub` is safe to share; `--module-keygen` prints the exact
`TrustedModuleKeys` entry to paste into settings.

Module payloads are **per-platform** when they carry native dependencies (the Code Vector Store
ships ONNX Runtime and sqlite-vec natives, for example). Publish for the target RID and pack on
that platform, or cross-publish the RID and pack from Windows.

## Installing a package

Two equivalent paths:

- **Dashboard:** MCP tab → **Modules** → **Upload package…** and choose the `.kaeomod` file
  (`POST /api/modules/upload`). The upload is verified before anything is written to disk.
- **Copy in place:** drop the `.kaeomod` where the host can read it and import it from the host
  process, or extract it to `Data/Modules/{id}/{version}/` yourself.

Verified packages are extracted to `{dataDirectory}/Modules/{id}/{version}/` and registered in the
module registry. Enable, disable, and configure them from the **MCP** tab in either GUI.

## Load policy

`ModuleLoadPolicy` in `settings.jsonc` decides what happens to an unsigned module — a loose DLL or
a `.kaeomod` without `manifest.sig`:

| Value | Behavior |
|-------|----------|
| `AllowUnsigned` (default) | Load unsigned modules without complaint |
| `Warn` | Load them, but log a warning for each |
| `RequireSigned` | Refuse unsigned modules; loose DLLs are always refused. Only packages signed by a key in `TrustedModuleKeys` load |

Every load path (loose DLL, package import, dashboard upload) goes through one verification seam,
so changing the policy is configuration-only — no rebuild.

## Trust list

Add the public keys you trust to `settings.jsonc`:

```jsonc
"ModuleLoadPolicy": "RequireSigned",
"TrustedModuleKeys": [
  { "Name": "kaeo-build", "PublicKey": "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE..." }
]
```

`PublicKey` is the base64 DER `SubjectPublicKeyInfo` of an ECDSA P-256 key — exactly what
`--module-keygen` prints, and the contents of the `.pub` file with PEM headers stripped. An empty
list trusts no signer, so `RequireSigned` then refuses every package.

The dashboard's module cards show each package's signature state (`Signed`, `Unsigned`, or
`Invalid`) and the signer key id, so an operator can see which trust entry a package matched.