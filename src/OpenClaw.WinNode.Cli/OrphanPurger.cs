using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OpenClaw.WinNode.Cli;

/// <summary>
/// Recovery path for the case where the user uninstalled the OpenClaw Companion
/// MSIX *without* first running the in-app "Reset &amp; remove" flow. MSIX has no
/// supported CustomUninstall hook for non-Store packages, so anything we wrote
/// outside the package container (WSL distros installed by the local-gateway
/// flow, files under <c>%APPDATA%\OpenClawTray\</c>, the openclaw:// URI
/// registration, the auto-start Run key) becomes orphaned after Remove-AppxPackage.
///
/// This class detects those orphans and (with <c>--confirm-destructive</c>)
/// removes them. Without that flag it dry-runs and emits JSON describing what
/// it would delete. Surfaced via <c>OpenClaw.WinNode.Cli --purge-wsl-orphans</c>.
///
/// Exit codes:
/// <list type="bullet">
/// <item><description>0 — no orphans found, or all detected orphans were removed.</description></item>
/// <item><description>1 — orphans were found but not removed (dry-run mode, missing
///   --confirm-destructive). The JSON report enumerates them.</description></item>
/// <item><description>2 — orphan removal was attempted and at least one item failed.</description></item>
/// </list>
/// </summary>
internal static class OrphanPurger
{
    /// <summary>
    /// Substrings that identify a WSL distro as belonging to the OpenClaw
    /// local-gateway flow. We match these case-insensitively against the
    /// distro name returned by <c>wsl --list --quiet</c>.
    /// </summary>
    /// <remarks>
    /// The local-gateway installer has used two naming conventions across the
    /// project's history:
    /// <list type="bullet">
    /// <item><description><c>OpenClawGateway</c> — the original PascalCase
    /// name used by the WSL gateway installer (still in production as of
    /// 2026-05; observed on Mike's dev box during the MSIX-E2E manual test
    /// prep).</description></item>
    /// <item><description><c>openclaw-*</c> — the newer kebab-case
    /// convention adopted for variants like <c>openclaw-local</c>,
    /// <c>openclaw-staging</c>.</description></item>
    /// </list>
    /// Match is case-insensitive because <c>wsl --list --quiet</c> echoes
    /// the user-specified case verbatim and we cannot rely on either form.
    /// </remarks>
    internal static readonly string[] OrphanWslDistroPatterns = new[]
    {
        "openclaw",   // matches both "openclaw-*" and "OpenClawGateway" case-insensitively
    };

    /// <summary>
    /// Retained for backward compatibility with <c>OrphanPurgerContractTests</c>
    /// and for any external script that pattern-matches the historical
    /// "openclaw-" prefix. New detection logic should use
    /// <see cref="OrphanWslDistroPatterns"/>.
    /// </summary>
    internal const string OrphanWslDistroPrefix = "openclaw-";

    /// <summary>
    /// Registry subkeys under <c>HKCU\Software\Classes</c> we treat as
    /// orphan URI scheme registrations. Both the lowercase
    /// <c>openclaw</c> form (which the unpackaged DeepLinkHandler writes)
    /// and the PascalCase <c>OpenClaw</c> form (observed in the wild from
    /// older builds) are listed because Windows Explorer-driven user
    /// scrubbers can leave one but not the other.
    /// </summary>
    internal static readonly string[] OrphanUriSchemeKeys = new[]
    {
        @"Software\Classes\openclaw",
        @"Software\Classes\OpenClaw",
    };

    public record OrphanItem(string Kind, string Name, string Detail);
    public record PurgeReport(
        IReadOnlyList<OrphanItem> Orphans,
        IReadOnlyList<OrphanItem> Removed,
        IReadOnlyList<OrphanItem> Failed,
        bool ConfirmDestructive);

    public static async Task<int> RunAsync(
        bool confirmDestructive,
        bool jsonOutput,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?>? envLookup = null)
    {
        envLookup ??= Environment.GetEnvironmentVariable;

        var orphans = new List<OrphanItem>();
        orphans.AddRange(DetectWslDistros(stderr));
        orphans.AddRange(DetectFileOrphans(envLookup));
        orphans.AddRange(DetectRegistryOrphans());

        var removed = new List<OrphanItem>();
        var failed = new List<OrphanItem>();

        if (confirmDestructive)
        {
            foreach (var orphan in orphans)
            {
                try
                {
                    await RemoveAsync(orphan, stderr);
                    removed.Add(orphan);
                }
                catch (Exception ex)
                {
                    failed.Add(orphan with { Detail = $"{orphan.Detail} (remove failed: {ex.Message})" });
                }
            }
        }

        var report = new PurgeReport(orphans, removed, failed, confirmDestructive);
        if (jsonOutput)
        {
            stdout.WriteLine(JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            WriteHumanReport(report, stdout);
        }

        if (failed.Count > 0) return 2;
        if (!confirmDestructive && orphans.Count > 0) return 1;
        return 0;
    }

    private static IEnumerable<OrphanItem> DetectWslDistros(TextWriter stderr)
    {
        // wsl.exe --list --quiet writes one distro name per line, encoded as
        // UTF-16LE without BOM (a known wsl.exe quirk). We force the codepage
        // via cmd /U-equivalent and read raw bytes, then strip the BOM-less
        // UTF-16.
        ProcessStartInfo psi;
        try
        {
            psi = new ProcessStartInfo("wsl.exe", "--list --quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode
            };
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"[purge] WSL not available ({ex.Message}); skipping distro detection.");
            yield break;
        }

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"[purge] wsl.exe failed to launch ({ex.Message}); skipping distro detection.");
            yield break;
        }
        if (proc is null) yield break;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            // Most likely "WSL has no distributions installed" — exit code 1
            // with empty output. Nothing to do.
            yield break;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().Trim('\u0000');
            if (line.Length == 0) continue;
            // Match against every documented pattern, case-insensitive. See
            // OrphanWslDistroPatterns for why we accept both PascalCase and
            // kebab-case forms.
            var matched = false;
            foreach (var pattern in OrphanWslDistroPatterns)
            {
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) continue;
            yield return new OrphanItem(
                Kind: "wsl-distro",
                Name: line,
                Detail: $"WSL distribution installed by the OpenClaw local-gateway flow");
        }
    }

    private static IEnumerable<OrphanItem> DetectFileOrphans(Func<string, string?> envLookup)
    {
        foreach (var candidate in new[]
        {
            (Env: "APPDATA",      Sub: "OpenClawTray", Kind: "appdata-folder"),
            (Env: "LOCALAPPDATA", Sub: "OpenClawTray", Kind: "localappdata-folder"),
        })
        {
            var root = envLookup(candidate.Env);
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, candidate.Sub);
            if (!Directory.Exists(path)) continue;

            long byteCount = 0;
            int fileCount = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    byteCount += new FileInfo(f).Length;
                }
            }
            catch { /* best-effort size; reporting still useful */ }

            yield return new OrphanItem(
                Kind: candidate.Kind,
                Name: path,
                Detail: $"{fileCount} file(s), {byteCount} byte(s)");
        }
    }

    private static IEnumerable<OrphanItem> DetectRegistryOrphans()
    {
        // openclaw:// URI scheme (unpackaged-only; PR #310 path). Packaged
        // installs use the windows.protocol manifest extension and there is
        // nothing in HKCU\Software\Classes for them. We check both casing
        // variants because the registry is case-insensitive for lookup but
        // can hold both keys simultaneously if different scrubbing scripts
        // touched them.
        foreach (var subkey in OrphanUriSchemeKeys)
        {
            if (TryRegistryKeyExists(Registry.CurrentUser, subkey, out var detail))
            {
                yield return new OrphanItem("registry-uri-scheme",
                    $@"HKCU\{subkey}",
                    detail);
            }
        }

        // HKCU\...\Run entry for the legacy auto-start path (now superseded by
        // the MSIX StartupTask extension; an orphan here would silently re-launch
        // the no-longer-installed exe at sign-in).
        if (TryRegistryValueExists(Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Run", "OpenClawTray", out var runDetail))
        {
            yield return new OrphanItem("registry-run-key",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\OpenClawTray",
                runDetail);
        }
    }

    private static bool TryRegistryKeyExists(RegistryKey root, string path, out string detail)
    {
        detail = "registry key present";
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRegistryValueExists(RegistryKey root, string path, string valueName, out string detail)
    {
        detail = $"value '{valueName}' present";
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            if (key == null) return false;
            return key.GetValue(valueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RemoveAsync(OrphanItem orphan, TextWriter stderr)
    {
        switch (orphan.Kind)
        {
            case "wsl-distro":
                await RunWslUnregister(orphan.Name, stderr);
                break;
            case "appdata-folder":
            case "localappdata-folder":
                Directory.Delete(orphan.Name, recursive: true);
                break;
            case "registry-uri-scheme":
                // Strip the HKCU\ prefix re-attached at detection time to
                // recover the original subkey path. We delete *that* subtree
                // rather than the hard-coded lowercase variant so the
                // PascalCase key (HKCU\Software\Classes\OpenClaw) also gets
                // removed when it's the one detected.
                var subkey = orphan.Name.StartsWith(@"HKCU\")
                    ? orphan.Name[5..]
                    : orphan.Name;
                Registry.CurrentUser.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false);
                break;
            case "registry-run-key":
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                {
                    key?.DeleteValue("OpenClawTray", throwOnMissingValue: false);
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown orphan kind: {orphan.Kind}");
        }
    }

    private static async Task RunWslUnregister(string distroName, TextWriter stderr)
    {
        // We deliberately do NOT shell out via cmd /c — wsl.exe arguments don't
        // need quoting in this case and going through cmd lets a maliciously
        // named distro inject extra commands.
        var psi = new ProcessStartInfo("wsl.exe", $"--unregister {distroName}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("wsl.exe failed to launch");
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"wsl --unregister {distroName} exited {proc.ExitCode}: {err.Trim()}");
        }
    }

    private static void WriteHumanReport(PurgeReport report, TextWriter stdout)
    {
        if (report.Orphans.Count == 0)
        {
            stdout.WriteLine("No OpenClaw orphans detected.");
            return;
        }

        stdout.WriteLine($"Detected {report.Orphans.Count} orphan(s):");
        foreach (var o in report.Orphans)
        {
            stdout.WriteLine($"  [{o.Kind}] {o.Name} — {o.Detail}");
        }

        if (!report.ConfirmDestructive)
        {
            stdout.WriteLine();
            stdout.WriteLine("Dry-run; pass --confirm-destructive to actually remove them.");
            return;
        }

        if (report.Removed.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine($"Removed {report.Removed.Count}:");
            foreach (var o in report.Removed)
            {
                stdout.WriteLine($"  [{o.Kind}] {o.Name}");
            }
        }
        if (report.Failed.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine($"Failed to remove {report.Failed.Count}:");
            foreach (var o in report.Failed)
            {
                stdout.WriteLine($"  [{o.Kind}] {o.Name} — {o.Detail}");
            }
        }
    }
}
