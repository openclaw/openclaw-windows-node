using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mcp;
using OpenClaw.Shared.Mxc;
using OpenClaw.TestSupport;

namespace OpenClaw.WinNode.Cli.Tests;

public sealed class FileWorkerProcessTests
{
    private const int OperationTimeoutMs = 30_000;

    [Fact]
    public async Task AllEightCommandsUseTheRealCompatibilityWorkerThroughMcpAndCli()
    {
        await using var fixture = new Fixture();
        var path = Path.Combine(fixture.Data, "sample.txt");
        var created = await fixture.Call("file.write", new { path, contentBase64 = "YWJj" });
        Assert.False(created.GetProperty("overwritten").GetBoolean());
        var stat = await fixture.Call("file.stat", new { path });
        Assert.Equal("file", stat.GetProperty("type").GetString());
        Assert.Equal(3, stat.GetProperty("size").GetInt64());
        var fetched = await fixture.Call("file.fetch", new { path, expectedBinding = stat.GetProperty("binding") });
        Assert.Equal("YWJj", fetched.GetProperty("base64").GetString());
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            fetched.GetProperty("sha256").GetString());
        var listed = await fixture.Call("dir.list", new { path = fixture.Data });
        Assert.Equal("sample.txt", Assert.Single(listed.GetProperty("entries").EnumerateArray()).GetProperty("name").GetString());
        var archive = await fixture.Call("dir.fetch", new { path = fixture.Data });
        Assert.Equal(1, archive.GetProperty("fileCount").GetInt32());
        Assert.Equal(archive.GetProperty("tarBytes").GetInt64(),
            Convert.FromBase64String(archive.GetProperty("tarBase64").GetString()!).LongLength);
        using (var compressed = new MemoryStream(Convert.FromBase64String(archive.GetProperty("tarBase64").GetString()!)))
        using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            Assert.Equal("./", tar.GetNextEntry()!.Name);
            var file = tar.GetNextEntry()!;
            Assert.Equal("./sample.txt", file.Name);
            Assert.Equal(TarEntryType.RegularFile, file.EntryType);
            using var content = new MemoryStream();
            await file.DataStream!.CopyToAsync(content);
            Assert.Equal("abc"u8.ToArray(), content.ToArray());
            Assert.Null(tar.GetNextEntry());
        }
        var directories = await fixture.Call("fs.listDir", new { path = fixture.Root });
        Assert.Contains(directories.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("path").GetString() == fixture.Data);
        var input = Path.Combine(fixture.Root, "input.bin");
        var bytes = Enumerable.Range(0, 1024 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(input, bytes);
        var destination = Path.Combine(fixture.Data, "streamed.bin");
        var streamCreate = await fixture.Call("file.create", new { path = destination }, "--input-file", input);
        Assert.Equal("created", streamCreate.GetProperty("status").GetString());
        Assert.Equal(bytes.LongLength, streamCreate.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), streamCreate.GetProperty("sha256").GetString());
        var exists = await fixture.Call("file.create", new { path = destination }, "--input-file", input);
        Assert.Equal("exists", exists.GetProperty("status").GetString());
        Assert.False(exists.TryGetProperty("sha256", out _));
        var output = Path.Combine(fixture.Root, "received.bin");
        await fixture.Call("file.fetch", new { path = destination, maxBytes = bytes.Length }, "--output-file", output);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
        var upload = await fixture.Call("terminal.upload", new { name = "fixture.txt", contentBase64 = "YWJj" });
        Assert.Equal(3, upload.GetProperty("size").GetInt64());
        Assert.Equal("abc", await File.ReadAllTextAsync(upload.GetProperty("path").GetString()!));
        string[] expectedCommands = ["file.stat", "file.fetch", "file.write", "file.create", "dir.list", "dir.fetch", "fs.listDir", "terminal.upload"];
        Assert.Equal(expectedCommands.Order(), fixture.Discovered.Order());
        Assert.Empty(Directory.GetDirectories(fixture.Scratch));
    }

    [Fact]
    public async Task RealWorkerSemanticFailureIsAnErrorExitWithoutChangingExistingData()
    {
        await using var fixture = new Fixture();
        var path = Path.Combine(fixture.Data, "existing");
        await File.WriteAllTextAsync(path, "keep");
        var result = await fixture.Run("file.write", new { path, contentBase64 = "YWJj" });
        Assert.Equal(1, result.Exit);
        Assert.Contains("EXISTS_NO_OVERWRITE", result.Error);
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
        var missing = await fixture.Run("file.fetch", new { path = Path.Combine(fixture.Data, "missing") });
        Assert.Equal(1, missing.Exit);
        Assert.Contains("NOT_FOUND", missing.Error);
        Assert.Empty(Directory.GetDirectories(fixture.Scratch));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new("openclaw-file-process-");
        private readonly McpHttpServer _server;
        public string Root => _directory.Path;
        public string Data { get; }
        public string Scratch { get; }
        public IReadOnlyList<string> Discovered { get; private set; } = [];

        public Fixture()
        {
            Data = Directory.CreateDirectory(_directory.Combine("data")).FullName;
            Scratch = _directory.Combine("scratch");
            var policies = new NodeSandboxPolicyStore(new SettingsData
            {
                SandboxTimeoutMs = OperationTimeoutMs,
                SandboxUploadStagingPath = _directory.Combine("uploads")
            });
            // This selects the documented compatibility path, not an actual host-support assertion.
            var executor = new NodeSandboxExecutor(policies, () => _directory.Combine("settings"),
                availabilityProvider: () => new MxcAvailability(false, false, false, [], isWindowsUnsupported: true),
                spawn: _ => throw new InvalidOperationException("Compatibility execution must not invoke MXC."),
                probe: _ => throw new InvalidOperationException("Compatibility execution must not probe MXC."),
                scratchRoot: Scratch, fileWorker: new Resolver());
            var capability = new FileCapability(NullLogger.Instance, new FileOperations(executor));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            _server = new McpHttpServer(new McpToolBridge(() => [capability]), port, NullLogger.Instance, "fixture-token");
            _server.Start();
        }

        public async Task<JsonElement> Call(string command, object arguments, params string[] options)
        {
            if (Discovered.Count == 0)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                var exit = await CliRunner.RunAsync(["--list-tools", "--mcp-url", _server.Endpoint], output, error,
                    EnvironmentValue);
                Assert.True(exit == 0, error.ToString());
                using var discovery = JsonDocument.Parse(output.ToString());
                Discovered = discovery.RootElement.GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("name").GetString()!).ToArray();
            }
            var result = await Run(command, arguments, options);
            Assert.True(result.Exit == 0, result.Error);
            using var document = JsonDocument.Parse(result.Output);
            return document.RootElement.Clone();
        }

        public async Task<(int Exit, string Output, string Error)> Run(string command, object arguments, params string[] options)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var args = new List<string>
            {
                "--command", command, "--params", JsonSerializer.Serialize(arguments),
                "--mcp-url", _server.Endpoint, "--invoke-timeout", OperationTimeoutMs.ToString(CultureInfo.InvariantCulture)
            };
            args.AddRange(options);
            var exit = await CliRunner.RunAsync(args.ToArray(), output, error,
                EnvironmentValue);
            return (exit, output.ToString(), error.ToString());
        }

        private string? EnvironmentValue(string key) => key switch
        {
            "OPENCLAW_MCP_TOKEN" => "fixture-token",
            "OPENCLAW_TRAY_DATA_DIR" => Root,
            _ => null
        };

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class Resolver : IFixedFileWorkerResolver
    {
        public FixedFileWorkerLease Resolve(string? ownedPayloadDirectory = null, CancellationToken ct = default)
        {
            var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
            if (root is null)
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                    directory = directory.Parent;
                root = directory?.FullName ?? throw new IOException("Cannot locate the worker build.");
            }
            var configuration = typeof(FileWorkerProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var path = Path.Combine(root, "src", "OpenClaw.FileWorker", "bin", configuration, "net10.0");
            if (!File.Exists(Path.Combine(path, "OpenClaw.FileWorker.exe")))
                throw new IOException("The referenced fixed worker was not built.");
            return new Lease(path);
        }
    }

    private sealed class Lease(string directory) : FixedFileWorkerLease
    {
        public override string ImagePath => Path.Combine(directory, "OpenClaw.FileWorker.exe");
        public override string PayloadDirectory => directory;
        public override void Dispose() { }
    }
}
