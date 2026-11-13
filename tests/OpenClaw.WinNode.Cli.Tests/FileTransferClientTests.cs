using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Shared;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.WinNode.Cli.Tests;

public class FileTransferClientTests
{
    [Fact]
    public async Task CreateStreamsRawInputAndReturnsNormalJsonThroughRealMcpListener()
    {
        await using var fixture = new Fixture();
        var input = Path.Combine(fixture.Directory, "input.bin");
        var bytes = Enumerable.Range(0, 1024 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(input, bytes);
        var (exit, output, error) = await fixture.Run("file.create", """{"path":"C:\\Fixture\\target.bin"}""", "--input-file", input);
        Assert.True(exit == 0, error);
        Assert.Equal(bytes, fixture.Capability.Created);
        using var result = JsonDocument.Parse(output);
        Assert.Equal("created", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("C:\\Fixture\\target.bin", fixture.Capability.RealArguments.GetProperty("expectedCanonicalPath").GetString());
        Assert.Equal("existing-parent", fixture.Capability.RealArguments.GetProperty("expectedBinding").GetProperty("anchorInode").GetString());
    }

    [Fact]
    public async Task CreateExistsReturnsSuccessWithoutInventingAContentMatch()
    {
        await using var fixture = new Fixture();
        fixture.Capability.ExistingTarget = true;
        var input = Path.Combine(fixture.Directory, "input.bin");
        await File.WriteAllBytesAsync(input, "abc"u8.ToArray());
        var (exit, output, error) = await fixture.Run("file.create",
            """{"path":"C:\\Fixture\\already-present.bin"}""", "--input-file", input);
        Assert.True(exit == 0, error);
        using var result = JsonDocument.Parse(output);
        Assert.Equal("exists", result.RootElement.GetProperty("status").GetString());
        Assert.False(result.RootElement.TryGetProperty("size", out _));
        Assert.False(result.RootElement.TryGetProperty("sha256", out _));
    }

    [Fact]
    public async Task FailureWhileCreateWaitsForAckExitsWithoutWaitingForDeadline()
    {
        await using var fixture = new Fixture();
        fixture.Capability.RejectFirstChunk = true;
        var input = Path.Combine(fixture.Directory, "input.bin");
        await File.WriteAllBytesAsync(input, "abc"u8.ToArray());
        var result = await fixture.Run("file.create",
            """{"path":"C:\\Fixture\\target.bin"}""", "--input-file", input)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.Exit);
        Assert.Contains("WRITE_ERROR", result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchValidatesHashBeforeNoClobberPublication(bool corrupt)
    {
        await using var fixture = new Fixture();
        fixture.Capability.CorruptHash = corrupt;
        fixture.Capability.Bytes = Enumerable.Repeat((byte)0xfb, 1024 * 1024 + 3).ToArray();
        var destination = Path.Combine(fixture.Directory, "result.bin");
        var (exit, output, error) = await fixture.Run("file.fetch",
            """{"path":"C:\\Fixture\\source.bin","maxBytes":2097152}""", "--output-file", destination);
        Assert.True(exit == (corrupt ? 1 : 0), error);
        if (corrupt)
        {
            Assert.False(File.Exists(destination));
            Assert.Contains("SHA256", error);
        }
        else
        {
            Assert.Equal(fixture.Capability.Bytes, await File.ReadAllBytesAsync(destination));
            Assert.True(JsonDocument.Parse(output).RootElement.GetProperty("ok").GetBoolean());
            var again = await fixture.Run("file.fetch", """{"path":"C:\\Fixture\\source.bin","maxBytes":2097152}""", "--output-file", destination);
            Assert.Equal(1, again.Exit);
            Assert.Equal(fixture.Capability.Bytes, await File.ReadAllBytesAsync(destination));
        }
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.partial"));
    }

    [Fact]
    public async Task ExplicitExpectedIdentityIsNotReplacedWithPreflightIdentity()
    {
        await using var fixture = new Fixture();
        var destination = Path.Combine(fixture.Directory, "result.bin");
        var (exit, _, error) = await fixture.Run("file.fetch",
            """{"path":"C:\\Fixture\\source.bin","maxBytes":1024,"expectedBinding":{"kind":"existing","device":"caller-device","inode":"caller-inode"},"expectedCanonicalPath":"C:\\Caller\\file.bin"}""",
            "--output-file", destination);
        Assert.True(exit == 0, error);
        var args = fixture.Capability.RealArguments;
        Assert.Equal("caller-device", args.GetProperty("expectedBinding").GetProperty("device").GetString());
        Assert.Equal("caller-inode", args.GetProperty("expectedBinding").GetProperty("inode").GetString());
        Assert.Equal("C:\\Caller\\file.bin", args.GetProperty("expectedCanonicalPath").GetString());
    }

    [Fact]
    public async Task FailedOperationBeforeReadinessExitsWithoutWaitingForTimeout()
    {
        await using var fixture = new Fixture();
        fixture.Capability.FailBeforeListen = true;
        var destination = Path.Combine(fixture.Directory, "result.bin");
        var result = await fixture.Run("file.fetch", """{"path":"C:\\Fixture\\source.bin","maxBytes":1024}""",
            "--output-file", destination).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.Exit);
        Assert.Contains("NOT_FOUND", result.Error);
        Assert.False(File.Exists(destination));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.partial"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":\"C:\\\\Fixture\\\\source.bin\"}")]
    public async Task MissingBinaryParametersReturnArgumentErrorAndRemoveOwnedPartial(string arguments)
    {
        await using var fixture = new Fixture();
        var destination = Path.Combine(fixture.Directory, "result.bin");
        var result = await fixture.Run("file.fetch", arguments, "--output-file", destination);
        Assert.Equal(2, result.Exit);
        Assert.False(File.Exists(destination));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.partial"));
    }

    [Fact]
    public async Task MissingOutputDirectoryReturnsErrorWithoutAttemptingUnownedCleanup()
    {
        await using var fixture = new Fixture();
        var destination = Path.Combine(fixture.Directory, "missing", "result.bin");
        var result = await fixture.Run("file.fetch", """{"path":"C:\\Fixture\\source.bin","maxBytes":1024}""",
            "--output-file", destination);
        Assert.Equal(1, result.Exit);
        Assert.False(System.IO.Directory.Exists(Path.GetDirectoryName(destination)));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly McpHttpServer _server;
        public string Directory { get; } = Path.Combine(Environment.CurrentDirectory, $".cli-files-{Guid.NewGuid():N}");
        public FileCapabilityFixture Capability { get; } = new();
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _server = new McpHttpServer(new McpToolBridge(() => [Capability]), port, NullLogger.Instance, "fixture-token");
            _server.Start();
        }
        public async Task<(int Exit, string Output, string Error)> Run(string command, string args, string option, string path)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await CliRunner.RunAsync(
                ["--command", command, "--params", args, option, path, "--mcp-url", _server.Endpoint, "--invoke-timeout", "20000"],
                stdout, stderr, key => key == "OPENCLAW_MCP_TOKEN" ? "fixture-token" : key == "OPENCLAW_TRAY_DATA_DIR" ? Directory : null);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class FileCapabilityFixture : INodeCapability
    {
        public byte[] Bytes { get; set; } = "fixture"u8.ToArray();
        public byte[]? Created { get; private set; }
        public bool CorruptHash { get; set; }
        public bool FailBeforeListen { get; set; }
        public bool ExistingTarget { get; set; }
        public bool RejectFirstChunk { get; set; }
        public JsonElement RealArguments { get; private set; }
        public string Category => "file";
        public IReadOnlyList<string> Commands => ["file.create", "file.fetch"];
        public bool CanHandle(string command) => Commands.Contains(command);
        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request) => ExecuteAsync(request, default);
        public async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request, CancellationToken ct)
        {
            var create = request.Command == "file.create";
            if (request.Args.TryGetProperty("preflightOnly", out var flag) && flag.GetBoolean())
                return new()
                {
                    Ok = true, Payload = new
                    {
                        ok = true, path = request.Args.GetProperty("path").GetString(),
                        binding = create ? (object)new { kind = "write", anchorPath = "C:\\Fixture", anchorDevice = "42", anchorInode = "existing-parent" }
                            : new { kind = "existing", device = "42", inode = "existing-file" }
                    }
                };
            RealArguments = request.Args.Clone();
            if (FailBeforeListen)
                return new() { Ok = true, Payload = new { ok = false }, FileOutcome = new(false, "NOT_FOUND", "Fixture file not found.") };
            // The fixture stands in for the worker-facing capability, not the framing/HTTP adapters.
            // Read the existing internal invocation field without widening the production API.
            var channel = (IFileInvocationChannel)typeof(NodeInvokeRequest)
                .GetProperty("FileChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(request)!;
            var messages = Channel.CreateBounded<byte[]>(2);
            await using var subscription = await channel.ListenAsync((message, _) =>
            {
                Assert.True(messages.Writer.TryWrite(message.ToArray()));
                return ValueTask.CompletedTask;
            }, ct);
            if (create)
            {
                using var output = new MemoryStream();
                while (true)
                {
                    var message = await messages.Reader.ReadAsync(ct);
                    if (message.Length == 0) break;
                    if (RejectFirstChunk)
                        return new()
                        {
                            Ok = true, Payload = new { ok = false },
                            FileOutcome = new(false, "WRITE_ERROR", "Fixture stopped before acknowledging input.")
                        };
                    Assert.InRange(message.Length, 1, 1024 * 1024);
                    output.Write(message);
                    await channel.SendAsync("ack"u8.ToArray(), ct);
                }
                Created = output.ToArray();
                Assert.Equal(Created.Length, request.Args.GetProperty("sizeBytes").GetInt64());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Created)), request.Args.GetProperty("expectedSha256").GetString());
            }
            else
            {
                Assert.Equal("start", Encoding.UTF8.GetString(await messages.Reader.ReadAsync(ct)));
                for (var i = 0; i < Bytes.Length; i += 1024 * 1024)
                {
                    await channel.SendAsync(Bytes.AsMemory(i, Math.Min(1024 * 1024, Bytes.Length - i)), ct);
                    Assert.Equal("ack", Encoding.UTF8.GetString(await messages.Reader.ReadAsync(ct)));
                }
            }
            var data = create ? Created! : Bytes;
            return new()
            {
                Ok = true, Payload = create && ExistingTarget ? (object)new { ok = true, status = "exists" } : new
                {
                    ok = true, size = data.Length, status = create ? "created" : null,
                    sha256 = CorruptHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(data))
                }, FileOutcome = new(true)
            };
        }
    }
}
