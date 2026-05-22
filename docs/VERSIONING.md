# Versioning in OpenClaw Windows Hub

## How Versioning Works

This project uses GitVersion for automatic semantic versioning based on git tags and commit history. The version is used in multiple places:

### Version Properties in .csproj

The project file (`OpenClaw.Tray.WinUI.csproj`) defines only the `<Version>` property:

```xml
<Version>0.3.0</Version>
```

Other version-related properties (`FileVersion` and `AssemblyVersion`) are **not** explicitly set in the csproj files. This is intentional.

### Automatic Version Derivation

When only `<Version>` is set in a .NET project:
- **AssemblyVersion**: Automatically set to the numeric part of `Version` (e.g., `0.3.0` → `0.3.0.0`)
- **FileVersion**: Automatically set to the numeric part of `Version` (e.g., `0.3.0` → `0.3.0.0`)
- **InformationalVersion**: Set to the full `Version` value including suffixes (e.g., `0.3.0-beta.1`)

This ensures all version properties stay in sync automatically.

### CI Build Process

During CI builds (`.github/workflows/ci.yml`), GitVersion determines the semantic version from git history and passes it to the build:

```bash
dotnet build -p:Version=${{ needs.test.outputs.semVer }}
```

This `-p:Version=...` argument overrides the `<Version>` property in the csproj, and consequently also sets `FileVersion` and `AssemblyVersion` to match.

### Auto-Updater Version Detection

The Updatum auto-updater determines the current application version by reading the **AssemblyVersion** from the running executable using:

```csharp
Assembly.GetExecutingAssembly().GetName().Version
```

This is why it's critical that `AssemblyVersion` (and `FileVersion`) match the semantic version - otherwise, the updater will get confused and keep offering the same update repeatedly.

## Historical Issue

Previously, the csproj files had hardcoded values:

```xml
<Version>0.3.0</Version>
<FileVersion>0.2.0</FileVersion>
<AssemblyVersion>0.2.0</AssemblyVersion>
```

This caused a version mismatch:
- The semantic version was 0.3.0
- But the file and assembly versions were stuck at 0.2.0
- Updatum would read 0.2.0 from the running EXE
- It would see 0.4.0 available on GitHub
- It would offer to update from "0.2.0" to "0.4.0" even though the user was already on 0.3.0 or 0.4.0

## Solution

By removing the hardcoded `FileVersion` and `AssemblyVersion` properties, they now automatically derive from `Version`. When CI overrides `Version` via command-line, all three properties are set correctly and consistently.

## Best Practices

1. **Never hardcode `FileVersion` or `AssemblyVersion` in the csproj** - let them auto-derive from `Version`
2. **Let GitVersion and CI control the version** - the csproj's `<Version>` is just a fallback for local development builds
3. **Test version detection** - after building, check the EXE properties to ensure FileVersion matches expectations
4. **Use semantic versioning** - tags should follow `v{major}.{minor}.{patch}` format (e.g., `v0.4.0`)
5. **Use `OpenClaw.Shared.AppVersionInfo` for any user-visible or wire-exposed version string** - never re-roll
   `typeof(...).Assembly.GetName().Version` or hardcode literals like `"v0.1.0"`. `AppVersionInfo` is the single
   source of truth driven by `<Version>`, used by the About page, Update dialog, support-context dump,
   `device.info` capability, MCP `serverVersion` handshake, and the update-check diagnostics.

## Runtime Version Resolution (AppVersionInfo)

`src/OpenClaw.Shared/AppVersionInfo.cs` exposes:

- `AppVersionInfo.Version` → bare string, e.g. `"0.4.7"`
- `AppVersionInfo.DisplayVersion` → `"v"` prefix, e.g. `"v0.4.7"`

It resolves the version by:

1. Looking for the `OpenClaw.Tray.WinUI` assembly in the current `AppDomain` (so `dotnet test` and CLI siblings
   still report the tray's version rather than the testhost / dotnet host).
2. Falling back to `Assembly.GetEntryAssembly()`, then to the Shared assembly.
3. Reading `AssemblyInformationalVersionAttribute` (preferred) or `AssemblyVersion`.
4. Stripping SourceLink build metadata (`+abc123`) **and** the SemVer pre-release suffix (`-beta.1`) so the
   displayed value matches what Updatum compares (Updatum reads the numeric `AssemblyVersion` only).

For tests that need a deterministic value regardless of host process, set the `internal` test hook:

```csharp
AppVersionInfo.TestOverride = "9.9.9";
```

## References

- [Microsoft Docs: Assembly Versioning](https://learn.microsoft.com/en-us/dotnet/standard/assembly/versioning)
- [Updatum Library](https://github.com/sn4k3/Updatum)
- [GitVersion Documentation](https://gitversion.net/)
