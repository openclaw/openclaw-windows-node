extern alias CacheResolver;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.FileTransfer;
using InstalledFileWorkerResolver = CacheResolver::OpenClaw.Shared.FileTransfer.InstalledFileWorkerResolver;

namespace OpenClaw.FileWorker.Tests;

[SupportedOSPlatform("windows")]
public sealed class WorkerCacheProcessTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task RealAppHostMappedWithoutResolverPinsVetoesEvictionUntilWorkerExits()
    {
        // CreateProcess limits the apphost path independently of native filesystem long-path support.
        var root = Path.Combine(Path.GetTempPath(), "openclaw-worker-cache-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(root);
        Process? worker = null;
        DateTime started = default;
        string? image = null;
        try
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var output = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!,
                "src", "OpenClaw.FileWorker", "bin", configuration, "net10.0");
            var files = Directory.GetFiles(output).Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".json")
                .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
            Assert.Contains("OpenClaw.FileWorker.exe", files.Keys);
            // This ordinary-host fixture uses the real built apphost and engine.
            // The self-contained distribution inventory is a separate package proof.
            files["coreclr.dll"] = "unused-framework-dependent-fixture"u8.ToArray();
            var old = CreateResolver(root, files, "old");
            using (var lease = old.Resolve(cache)) image = lease.ImagePath;
            var entry = Path.GetDirectoryName(image)!;
            var inventory = Directory.GetFiles(entry).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
            for (var index = 0; index < 7; index++) Directory.CreateDirectory(Path.Combine(cache, "unknown-" + index));
            var data = Path.Combine(root, "data");
            File.WriteAllText(data, "abc");
            var admission = new FileWorkerAdmission("host", "windows_unsupported", 1, "cache-kernel-fixture", null, root, Path.Combine(root, "uploads"));
            FileBinding binding;
            using (var fs = new RetainedFileSystem(admission)) binding = fs.Open(data).Binding;
            var start = new ProcessStartInfo(image)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root, CreateNoWindow = true,
            };
            start.ArgumentList.Add("--stdio");
            worker = Process.Start(start)!;
            started = worker.StartTime;
            var evidence = Environment.GetEnvironmentVariable("RUNNER_TEMP");
            File.WriteAllText(Path.Combine(string.IsNullOrWhiteSpace(evidence) ? root : evidence, "worker-cache-process-" + worker.Id + ".json"),
                JsonSerializer.Serialize(new { Pid = worker.Id, StartTime = started.ToUniversalTime(), Executable = image,
                    Arguments = new[] { "--stdio" }, WorkingDirectory = root, Ports = Array.Empty<int>(),
                    Data = start.Environment.TryGetValue("OPENCLAW_TRAY_DATA_DIR", out var dataRoot) ? dataRoot : null,
                    AppData = start.Environment.TryGetValue("OPENCLAW_TRAY_APPDATA_DIR", out var appDataRoot) ? appDataRoot : null,
                    LocalAppData = start.Environment.TryGetValue("OPENCLAW_TRAY_LOCALAPPDATA_DIR", out var localDataRoot) ? localDataRoot : null }));
            FileWorkerProtocol.WriteJson(worker.StandardInput.BaseStream, FilePacketKind.Request,
                new FileWorkerRequest(FileWorkerProtocol.Version, admission, new(FileOperation.Fetch, data, Binary: true,
                    ExpectedBinding: binding, ExpectedCanonicalPath: data)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ready = await FileWorkerProtocol.ReadAsync(worker.StandardOutput.BaseStream, timeout.Token);
            Assert.Equal(FilePacketKind.Listen, ready.Kind);
            Assert.False(worker.HasExited);
            using (var exclusive = CreateFileW(@"\\?\" + image, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                Assert.False(exclusive.IsInvalid);
                var delete = 1;
                Assert.False(SetFileInformationByHandle(exclusive, 4, ref delete, 4));
                Assert.Equal(5, Marshal.GetLastWin32Error());
            }
            // No resolver lease remains. The native image-section veto, not a
            // parent file pin or fake SDK termination receipt, must preserve it.
            var next = CreateResolver(root, files, "new");
            Assert.Contains("cache is full", Assert.Throws<NotSupportedException>(() => next.Resolve(cache)).Message);
            Assert.Equal(inventory.Keys.Order(), Directory.GetFiles(entry).Select(Path.GetFileName).Order());
            foreach (var (name, bytes) in inventory) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(entry, name!)));
            using (var reusable = old.Resolve(cache)) Assert.Equal(entry, reusable.PayloadDirectory);
            worker.StandardInput.Close();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, worker.ExitCode);
            var ended = FileWorkerProtocol.Json<FileOperationResult>(
                await FileWorkerProtocol.ReadAsync(worker.StandardOutput.BaseStream, timeout.Token));
            Assert.False(ended.Outcome.Ok); // Deliberate EOF while awaiting "start".
            using var admitted = next.Resolve(cache);
            Assert.False(Directory.Exists(entry));
            Assert.Equal(8, Directory.GetDirectories(cache).Length);
        }
        catch (Exception ex)
        {
            output.WriteLine(ex.ToString());
            throw;
        }
        finally
        {
            if (worker is not null)
            {
                if (!worker.HasExited)
                {
                    Assert.Equal(started, worker.StartTime);
                    Assert.Equal(image, worker.MainModule!.FileName, ignoreCase: true);
                    worker.Kill();
                    await worker.WaitForExitAsync();
                }
                worker.Dispose();
            }
            Directory.Delete(root, true);
        }
    }

    private static InstalledFileWorkerResolver CreateResolver(string root, Dictionary<string, byte[]> files, string version)
    {
        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, rid, productVersion = version,
            files = files.ToDictionary(pair => pair.Key, pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value)))
        });
        var product = Path.Combine(root, "product");
        var payload = Directory.CreateDirectory(Path.Combine(product, "Workers", "FileWorker", rid,
            Convert.ToHexStringLower(SHA256.HashData(bytes))[..32])).FullName;
        foreach (var (name, value) in files) File.WriteAllBytes(Path.Combine(payload, name), value);
        File.WriteAllBytes(Path.Combine(payload, "payload-manifest.json"), bytes);
        var constructor = typeof(InstalledFileWorkerResolver).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(Assembly), typeof(string)], null)!;
        return (InstalledFileWorkerResolver)constructor.Invoke([new ManifestAssembly(bytes), product]);
    }
    private sealed class ManifestAssembly(byte[] bytes) : Assembly
    {
        public override Stream GetManifestResourceStream(string name) => new MemoryStream(bytes, writable: false);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref int value, uint size);
}
