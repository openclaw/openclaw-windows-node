using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Security.Extensions;
using Octokit;
using OpenClaw.Connection;
using Updatum;

internal static class Program
{
    private static readonly string[] OwnedBinaries =
    [
        "OpenClaw.Tray.WinUI.exe", "OpenClaw.Tray.WinUI.dll", "OpenClaw.Chat.dll",
        "OpenClaw.Connection.dll", "OpenClaw.SetupEngine.UI.dll", "OpenClaw.SetupEngine.dll",
        "OpenClaw.Shared.dll", "OpenClawTray.FunctionalUI.dll"
    ];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is not [var mode, var archivePath, var digest])
                throw new ArgumentException("Expected: verify|install <archive> <sha256 digest>.");
            using var identity = WindowsIdentity.GetCurrent();
            Console.WriteLine($"Proof token administrator-enabled: {new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}");
            if (mode == "install")
            {
                await InstallAsync(archivePath, digest);
                throw new InvalidOperationException("Updatum did not terminate its isolated child.");
            }
            Require(mode == "verify", "Unknown proof mode.");
            await VerifyAsync(archivePath, digest);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task VerifyAsync(string archivePath, string digest)
    {
        var root = Directory.CreateTempSubdirectory("openclaw-update-proof-").FullName;
        var complete = false;
        try
        {
            var package = Path.Combine(root, Path.GetFileName(archivePath));
            File.Copy(archivePath, package);
            using (var verified = await WindowsUpdatePackageVerifier.VerifyAsync(package, new FileInfo(package).Length, digest))
            {
                using var reader = ZipFile.OpenRead(package);
                Require(reader.Entries.Count > 8, "Expected a complete published payload.");
                ExpectIOException(() => File.OpenWrite(package).Dispose());
                ExpectIOException(() => File.Delete(package));
            }
            Console.WriteLine($"PASS native signed archive and read lease: {Path.GetFileName(package)}");

            var originals = new Dictionary<string, byte[]>();
            using (var archive = ZipFile.OpenRead(package))
            {
                foreach (var name in OwnedBinaries)
                {
                    using var entry = (archive.GetEntry(name) ?? throw new InvalidDataException(name)).Open();
                    using var bytes = new MemoryStream();
                    await entry.CopyToAsync(bytes);
                    originals.Add(name, bytes.ToArray());
                }
            }
            var changed = originals[OwnedBinaries[0]].ToArray();
            changed[64] ^= 1;
            await RejectPayloadAsync(root, originals, changed, "tampered");
            var microsoft = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
            using (var file = File.OpenRead(microsoft))
            {
                var signature = FileSignatureInfo.GetFromFileStream(file);
                using var signer = signature.SigningCertificate;
                using var timestamp = signature.TimestampCertificate;
                Require(signature.State == SignatureState.SignedAndTrusted, "Microsoft fixture must be trusted.");
            }
            await RejectPayloadAsync(root, originals, File.ReadAllBytes(microsoft), "wrong-publisher");

            var noTimestamp = TimestampFixture.Remove(originals[OwnedBinaries[0]]);
            var timestampPath = Path.Combine(root, "without-timestamp.exe");
            File.WriteAllBytes(timestampPath, noTimestamp);
            using (var file = File.OpenRead(timestampPath))
            {
                var signature = FileSignatureInfo.GetFromFileStream(file);
                using var signer = signature.SigningCertificate;
                using var timestamp = signature.TimestampCertificate;
                Require(signer is not null && timestamp is null, "Timestamp removal must preserve the signer.");
                Console.WriteLine($"Timestamp-removed native state: {signature.State}. Expired signer trust can reject before timestamp policy.");
            }
            await RejectPayloadAsync(root, originals, noTimestamp, "no-timestamp");
            await HandoffAsync(root, package, digest, originals);
            complete = true;
        }
        finally
        {
            // Preserve failed handoff evidence instead of deleting a directory
            // while an updater child may still be using it.
            if (complete) Directory.Delete(root, recursive: true);
            else Console.Error.WriteLine($"Preserved failed proof directory: {root}");
        }
    }

    private static async Task RejectPayloadAsync(
        string root, Dictionary<string, byte[]> originals, byte[] replacement, string name)
    {
        var path = Path.Combine(root, name + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var item in originals)
            {
                using var entry = archive.CreateEntry(item.Key).Open();
                var bytes = item.Key == OwnedBinaries[0] ? replacement : item.Value;
                entry.Write(bytes);
            }
        }
        // Recompute only the synthetic negative fixture's digest, so this check
        // must reach native signature verification rather than stop at hashing.
        try
        {
            using var verified = await WindowsUpdatePackageVerifier.VerifyAsync(
                path, new FileInfo(path).Length, Digest(path));
        }
        catch (InvalidDataException error) when (error.Message.Contains("Update verification rejected", StringComparison.Ordinal))
        {
            Console.WriteLine($"PASS native rejection: {name}: {error.Message}");
            return;
        }
        throw new InvalidOperationException($"Native verification accepted {name}.");
    }

    private static async Task HandoffAsync(
        string root, string package, string digest, Dictionary<string, byte[]> originals)
    {
        var app = Path.Combine(root, "isolated app");
        var temp = Path.Combine(root, "private temp");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(temp);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(app, Path.GetRelativePath(AppContext.BaseDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var assembly = Assembly.GetExecutingAssembly().GetName().Name!;
        var executable = Path.Combine(app, assembly + ".exe");
        Require(File.Exists(executable), "Proof needs the unique apphost.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = app
        };
        start.ArgumentList.Add("install");
        start.ArgumentList.Add(package);
        start.ArgumentList.Add(digest);
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Proof child did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await child.WaitForExitAsync(timeout.Token);
        Require(child.ExitCode == 0, $"Proof child failed: {child.ExitCode}");

        var extraction = Path.Combine(temp, Path.GetFileNameWithoutExtension(package) + "-UpdateExtracted");
        var script = Path.Combine(temp, Path.GetFileNameWithoutExtension(package) + "-UpdatumAutoUpgrade.bat");
        string? retainedBatchDigest = null;
        const string expectedSelfDelete = "  start \"\" /b cmd /c \"timeout /t 1 >nul & call :DeleteIfSafe \"\"%~f0\"\" \"\"- Removing self\"\"\"";
        try
        {
            while (File.Exists(package) || Directory.Exists(extraction) || File.Exists(script))
                await Task.Delay(100, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var archiveRemains = File.Exists(package);
            var extractionRemains = Directory.Exists(extraction);
            var batchRemains = File.Exists(script);
            Console.WriteLine($"Dependency cleanup state: archive={archiveRemains}, extraction={extractionRemains}, batch={batchRemains}");
            Require(!archiveRemains && !extractionRemains, "Updatum did not remove its archive and extracted payload.");
            if (batchRemains)
            {
                // Updatum 1.3.4 starts a new cmd for this label-based self-delete.
                // Bind that separate residual to its actual generated bytes.
                var bytes = File.ReadAllBytes(script);
                var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
                Require(lines.Any(line => line.StartsWith("REM Autogenerated by UpdatumManager v1.3.4 [", StringComparison.Ordinal)) &&
                    lines.Contains(expectedSelfDelete), "Unexpected dependency batch cleanup implementation.");
                retainedBatchDigest = Convert.ToHexString(SHA256.HashData(bytes));
            }
        }

        Require(File.Exists(Path.Combine(app, "verified-before-handoff")), "Verifier cleanup checkpoint missing.");
        foreach (var item in originals)
            Require(File.ReadAllBytes(Path.Combine(app, item.Key)).SequenceEqual(item.Value), $"Installed bytes differ: {item.Key}");
        Require(!Directory.EnumerateDirectories(temp, "openclaw-update-verification-*").Any(), "Verifier directory survived handoff.");
        if (retainedBatchDigest is not null)
        {
            Console.WriteLine($"FOLLOW-UP Updatum 1.3.4 retained only its generated batch after successful copy and required cleanup; batch SHA256={retainedBatchDigest}");
            Console.WriteLine($"Pinned self-delete line: {expectedSelfDelete}");
        }
        Console.WriteLine("PASS actual Updatum extraction, isolated copy, and archive/extraction/verifier cleanup; application relaunch disabled.");
    }

    private static async Task InstallAsync(string path, string digest)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName().Name!;
        Require(assembly.StartsWith("OpenClaw.Update.TestHost.", StringComparison.Ordinal) &&
            Guid.TryParseExact(assembly["OpenClaw.Update.TestHost.".Length..], "N", out _), "Proof assembly must have a UUID.");
        Require(Path.GetFileName(Environment.ProcessPath) == assembly + ".exe", "Installer must run only in the unique apphost.");
        Require(Path.GetFullPath(EntryApplication.BaseDirectory!) == Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            "Updatum target must be the isolated app directory.");
        using var verified = await WindowsUpdatePackageVerifier.VerifyAsync(path, new FileInfo(path).Length, digest);
        using var updater = new UpdatumManager("openclaw", "openclaw-windows-node")
        {
            InstallUpdateSingleFileExecutableName = "OpenClaw.Tray.WinUI"
        };
        updater.InstallUpdateCompleted += (_, _) =>
        {
            Require(!Directory.EnumerateDirectories(Path.GetTempPath(), "openclaw-update-verification-*").Any(),
                "Verifier copies must be gone before Updatum terminates the process.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "verified-before-handoff"), "verified");
        };
        var name = Path.GetFileName(path);
        var asset = new ReleaseAsset("", 42, "", name, "", "uploaded", "application/zip",
            checked((int)new FileInfo(path).Length), 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "", new Author());
        var release = new Release("", "", "", "", 21, "", "v2026.9.4", "main", "", "", false, false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new Author(), "", "", [asset]);
        await updater.InstallUpdateAsync(new UpdatumDownloadedAsset(release, asset, path),
            runArguments: UpdatumManager.NoRunAfterUpgradeToken);
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ExpectIOException(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Verified archive was mutable while its lease was held.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
