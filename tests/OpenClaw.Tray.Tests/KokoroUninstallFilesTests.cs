using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenClaw.Shared.Audio;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class KokoroUninstallFilesTests
{
    // SHA-256 of the independent three-byte fixture "abc". Catalog size deliberately differs.
    private const string FixtureHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static readonly KokoroDownloadAsset[] Assets =
    [
        new("fixture", "model.onnx", "https://example.invalid/model", FixtureHash, 999),
        new("fixture", "voices.bin", "https://example.invalid/voices", FixtureHash, 999),
    ];
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_MODEL_UNINSTALL_TEST_TIMEOUT_SECONDS"), out var seconds)
            && seconds > 0 ? seconds : 120);

    [Fact]
    public void Preview_MeasuresFilesRatherThanCatalog_AndPreservesEverything()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var path = Seed(temp, 0);
        var unknown = Path.Combine(Path.GetDirectoryName(path)!, "personal.onnx");
        File.WriteAllText(unknown, "unrelated");
        File.WriteAllText(temp.Combine("settings.json"), "settings");

        var result = Inspect(temp, 0);

        Assert.True(result.Complete);
        Assert.Equal(1, result.EligibleMask);
        Assert.Equal(3, result.VerifiedBytes);
        Assert.Equal(3, result.RetainedBytes);
        Assert.Equal(0, result.RemovedBytes);
        Assert.Equal("abc", File.ReadAllText(path));
        Assert.Equal("unrelated", File.ReadAllText(unknown));
        Assert.Equal("settings", File.ReadAllText(temp.Combine("settings.json")));
        Assert.Contains(path, result.Text);
        Assert.Contains("sizes were not scanned", result.Text);
    }

    [Fact]
    public void Remove_OnlyPreviewSelection_PreservesUnselectedAndUnrelatedFiles()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var first = Seed(temp, 0);
        var selection = Inspect(temp, 0).EligibleMask;
        var later = Seed(temp, 1); // Downloaded after the preview. Not consented.
        var unknown = Path.Combine(Path.GetDirectoryName(first)!, "notes.txt");
        File.WriteAllText(unknown, "keep");
        File.WriteAllText(temp.Combine("mcp-token.txt"), "fixture-token");

        var result = Inspect(temp, selection);

        Assert.True(result.Complete);
        Assert.Equal(3, result.RemovedBytes);
        Assert.Equal(3, result.RetainedBytes);
        Assert.False(File.Exists(first));
        Assert.Equal("abc", File.ReadAllText(later));
        Assert.Equal("keep", File.ReadAllText(unknown));
        Assert.Equal("fixture-token", File.ReadAllText(temp.Combine("mcp-token.txt")));
        Assert.True(Directory.Exists(Path.GetDirectoryName(first)));
    }

    [Fact]
    public void Remove_RechecksContentAfterPreview_AndReportsChangedFileRetained()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var path = Seed(temp, 0);
        var selection = Inspect(temp, 0).EligibleMask;
        File.WriteAllText(path, "user model");

        var result = Inspect(temp, selection);

        Assert.False(result.Complete);
        Assert.Equal(0, result.RemovedBytes);
        Assert.Equal(10, result.RetainedBytes);
        Assert.Equal("user model", File.ReadAllText(path));
        Assert.Contains("content not verified", result.Text);
    }

    [Fact]
    public void Remove_MissingSelectedFile_IsNotCountedAsRemoved()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var path = Seed(temp, 0);
        File.Delete(path);
        var result = Inspect(temp, 1);
        Assert.True(result.Complete);
        Assert.Equal(0, result.RemovedBytes);
        Assert.Contains("Absent: " + path, result.Text);
    }

    [Fact]
    public void LockedFile_DoesNotHidePartialScanOrPreventOtherSelectedCleanup()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var first = Seed(temp, 0);
        var second = Seed(temp, 1);
        using (var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = Inspect(temp, 3);
            Assert.False(result.Complete);
            Assert.Equal(3, result.RemovedBytes);
            Assert.Contains("size unknown", result.Text);
            Assert.Contains(first, result.Text);
        }
        Assert.Equal("abc", File.ReadAllText(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void ReadOnlyFile_ReportsRemovalFailureWithoutClaimingRemovedBytes()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var path = Seed(temp, 0);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = Inspect(temp, 1);
            Assert.False(result.Complete);
            Assert.Equal(0, result.RemovedBytes);
            Assert.True(File.Exists(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationOrExpiredBudget_PreservesFilesAndMarksIncomplete(bool cancel)
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var path = Seed(temp, 0);
        var result = KokoroUninstallFiles.Inspect(temp.Path, Assets, 1,
            new CancellationToken(cancel), cancel ? TestTimeout : TimeSpan.Zero);
        Assert.False(result.Complete);
        Assert.Equal(0, result.RemovedBytes);
        Assert.Equal("abc", File.ReadAllText(path));
        Assert.Contains(cancel ? "cancelled" : "deadline", result.Text);
    }

    [Fact]
    public void HardLinkedCatalogFile_IsRetainedEvenWhenItsContentsMatch()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        var path = Seed(temp, 0);
        var other = temp.Combine("personal-copy.onnx");
        Assert.True(CreateHardLink(other, path, IntPtr.Zero), $"Windows error {Marshal.GetLastWin32Error()}");
        var result = Inspect(temp, 1);
        Assert.False(result.Complete);
        Assert.Equal(0, result.RemovedBytes);
        Assert.Equal("abc", File.ReadAllText(path));
        Assert.Equal("abc", File.ReadAllText(other));
    }

    [Fact]
    public async Task RedirectedModelAncestor_IsNotTraversedOrDeleted()
    {
        using var temp = new TempDirectory("kokoro-uninstall-");
        using var external = new TempDirectory("kokoro-unrelated-");
        var path = Seed(external, 0);
        var link = temp.Combine("models");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{external.Combine("models")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        try { await process.WaitForExitAsync().WaitAsync(TestTimeout); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = Inspect(temp, 1);
            Assert.False(result.Complete);
            Assert.Equal(0, result.RemovedBytes);
            Assert.Equal(0, result.VerifiedBytes);
            Assert.Equal("abc", File.ReadAllText(path));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void PinnedAncestor_DeniesReparseWriterBeforeDescendantIsOpened()
    {
        using var temp = new TempDirectory("kokoro-ancestor-pin-");
        var ancestor = temp.Combine("models");
        Directory.CreateDirectory(ancestor);
        var handles = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
        // Exercise the actual native pin boundary without timing a racing thread or
        // adding a production callback. The directory is empty, so a reparse writer
        // would otherwise be able to redirect the next descendant lookup.
        var pin = typeof(KokoroUninstallFiles).GetMethod("LockAncestors",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pin);
        try
        {
            pin.Invoke(null, [ancestor, handles]);
            using var writer = OpenDirectoryWriter(ancestor, 0x40000000, 7, IntPtr.Zero,
                3, 0x02200000, IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();
            Assert.True(writer.IsInvalid);
            Assert.Equal(32, error); // ERROR_SHARING_VIOLATION, before FSCTL_SET_REPARSE_POINT.
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
        using var afterRelease = OpenDirectoryWriter(ancestor, 0x40000000, 7, IntPtr.Zero,
            3, 0x02200000, IntPtr.Zero);
        Assert.False(afterRelease.IsInvalid); // Fixture permits the operation without pins.
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryWriter(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [Fact]
    public void InstallerCatalog_CoversExactlyTheDownloadConsumers()
    {
        var downloads = KokoroModelManager.AvailablePacks.SelectMany(pack =>
            new[] { pack.Model, pack.VoicesFile }.Select(asset =>
                (pack.PackId, asset.FileName, asset.DownloadUrl, asset.Sha256, asset.SizeBytes))).ToArray();
        var cleanup = KokoroDownloadCatalog.All.Select(asset =>
            (asset.PackId, asset.FileName, asset.Url, asset.Sha256, asset.SizeBytes)).ToArray();
        Assert.Equal(downloads, cleanup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerShell51_RunsPreviewOrHonorsCancellationBeforeStarting(bool cancel)
    {
        using var temp = new TempDirectory("kokoro-uninstall-host-");
        var root = TestRepositoryPaths.GetRepositoryRoot();
        foreach (var name in new[] { "KokoroDownloadCatalog.cs", "KokoroUninstallFiles.cs" })
            File.Copy(Path.Combine(root, "src", "OpenClaw.Shared", "Audio", name), temp.Combine(name));
        File.Copy(Path.Combine(root, "scripts", "Uninstall-DownloadedModels.ps1"),
            temp.Combine("Uninstall-DownloadedModels.ps1"));
        var data = temp.Combine("data with spaces");
        Directory.CreateDirectory(data);
        var output = await UninstallScriptHost.RunAsync(temp, ["ConvertTo-CleanProcessOutput"], """
            $cancelPath = Join-Path $AppRoot 'cancel.txt'
            $expected = 0
            if ($env:OPENCLAW_TEST_CANCEL_MODELS -eq 'True') {
                [IO.File]::WriteAllText($cancelPath, 'cancel')
                $expected = 2
            }
            & (Join-Path $AppRoot 'Uninstall-DownloadedModels.ps1') -DataDirectory (Join-Path $AppRoot 'data with spaces') -ReportPath (Join-Path $AppRoot 'report.txt') -SelectionPath (Join-Path $AppRoot 'selection.txt') -CancellationPath $cancelPath
            if ($LASTEXITCODE -ne $expected) { throw "Unexpected model helper result: $LASTEXITCODE" }
            Write-Output ([IO.File]::ReadAllText((Join-Path $AppRoot 'selection.txt')))
            Write-Output ([IO.File]::ReadAllText((Join-Path $AppRoot 'report.txt')))
            """, new Dictionary<string, string?> { ["OPENCLAW_TEST_CANCEL_MODELS"] = cancel.ToString() });
        Assert.StartsWith("0", output.Trim());
        Assert.Contains(cancel ? "cancelled before starting" : "Absent:", output);
        Assert.Contains(data, output);
        if (!cancel) Assert.Contains("sizes were not scanned", output);
    }

    private static KokoroUninstallReport Inspect(TempDirectory temp, int selection) =>
        KokoroUninstallFiles.Inspect(temp.Path, Assets, selection, CancellationToken.None, TestTimeout);

    private static string Seed(TempDirectory temp, int index)
    {
        var directory = temp.Combine("models", "kokoro", "fixture");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Assets[index].FileName);
        File.WriteAllText(path, "abc");
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
