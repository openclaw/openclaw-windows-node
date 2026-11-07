using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.FileTransfer;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Tests.FileTransfer;

public sealed class FileContractTests
{
    // Independent literals from openclaw/openclaw@82df040c9c584fce0decbac9ebf260ec134e04c4,
    // extensions/file-transfer/src/shared/base64.ts and file-create-protocol.ts.
    [Theory]
    [InlineData("", "")]
    [InlineData("YQ==", "61")]
    [InlineData("YQ", "61")]
    [InlineData("YWI=", "6162")]
    [InlineData("YWI", "6162")]
    [InlineData("YWJj", "616263")]
    [InlineData("+/8=", "FBFF")]
    [InlineData("-_8", "FBFF")]
    public void WriteAcceptsCanonicalStandardOrUrlSafePaddedOrUnpadded(string input, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(StrictFileBase64.Decode(input)));

    [Theory]
    [InlineData("Y")]
    [InlineData("YQ=")]
    [InlineData("YWI==")]
    [InlineData("YQ==\n")]
    [InlineData("Y Q==")]
    [InlineData("YR==")]
    [InlineData("YWJ=")]
    [InlineData("%%%%")]
    public void NoncanonicalBase64IsRejectedBeforeDecode(string input) =>
        Assert.Equal("INVALID_BASE64", Assert.Throws<FileCommandException>(() => StrictFileBase64.Decode(input)).Code);

    [Theory]
    [InlineData("YQ")]
    [InlineData("-_8=")]
    [InlineData("YR==")]
    public void UploadRequiresCanonicalPaddedStandardAlphabet(string input) =>
        Assert.Throws<FileCommandException>(() => StrictFileBase64.Decode(input, standardPadded: true));

    [Fact]
    public void DecodedByteLimitIsCheckedBeforeAllocatingContent()
    {
        Assert.Equal(3, StrictFileBase64.Inspect("YWJj", 3, false));
        Assert.Equal("FILE_TOO_LARGE", Assert.Throws<FileCommandException>(() => StrictFileBase64.Inspect("YWJj", 2, false)).Code);
    }

    [Theory]
    [InlineData("{\"sizeBytes\":0,\"maxBytes\":0,\"expectedSha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\"}", true)]
    [InlineData("{\"sizeBytes\":3,\"maxBytes\":3,\"expectedSha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}", true)]
    [InlineData("{\"sizeBytes\":1,\"maxBytes\":0,\"expectedSha256\":\"ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb\"}", false)]
    [InlineData("{\"sizeBytes\":-1,\"expectedSha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\"}", false)]
    [InlineData("{\"sizeBytes\":0.5,\"expectedSha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\"}", false)]
    [InlineData("{\"sizeBytes\":0,\"expectedSha256\":\"not-a-digest\"}", false)]
    public void CreateMetadataUsesSafeIntegersAndLiteralDigestContract(string metadata, bool valid)
    {
        using var args = JsonDocument.Parse("{\"path\":\"C:\\\\Fixture\\\\file\"," + metadata[1..]);
        if (valid)
        {
            var parsed = FileCommandParser.Parse("file.create", args.RootElement);
            Assert.Equal(parsed.ExpectedSha256!.ToLowerInvariant(), parsed.ExpectedSha256);
        }
        else Assert.Throws<FileCommandException>(() => FileCommandParser.Parse("file.create", args.RootElement));
    }

    [Fact]
    public void AdminAdaptersValidateOnlyTheirOwnFieldsAndBoundUploadNameBeforeSanitizing()
    {
        var upload = FileCommandParser.Parse("terminal.upload", FileJson.Element(new
        {
            name = new string('a', 255), contentBase64 = "", path = 3, preflightOnly = "ignored", maxEntries = false
        }));
        Assert.Equal(255, upload.Name!.Length);
        foreach (var name in new[] { "", new string('a', 256) })
            Assert.Equal("INVALID_PARAMS", Assert.Throws<FileCommandException>(() => FileCommandParser.Parse("terminal.upload",
                FileJson.Element(new { name, contentBase64 = "" }))).Code);
        var listing = FileCommandParser.Parse("fs.listDir", FileJson.Element(new
        {
            path = @"C:\files", expectedBinding = 3, preflightOnly = "ignored", contentBase64 = false, sizeBytes = -1
        }));
        Assert.Equal(@"C:\files", listing.Path);
        Assert.Throws<FileCommandException>(() => FileCommandParser.Parse("fs.listDir", FileJson.Element(new { path = false })));
        Assert.Throws<FileCommandException>(() => FileCommandParser.Parse("file.fetch", FileJson.Element(new { path = @"C:\file", maxBytes = "3" })));
    }

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(true, false, "INVALID_REQUEST")]
    public void CreateReceiveFailureHasOuterInvalidRequestWhilePreflightRemainsInner(bool streamFailure, bool ok, string? code)
    {
        var result = new FileOperationResult(FileJson.Element(new { ok = false, code = "INTEGRITY_FAILURE", message = "Digest mismatch." }),
            new(false, "INTEGRITY_FAILURE", "Digest mismatch."), InvalidRequest: streamFailure);
        var response = FileCapability.Shape(new() { Id = "create", Command = "file.create" }, result);
        Assert.Equal(ok, response.Ok);
        Assert.Equal(code, response.ErrorCode);
        Assert.False(response.FileOutcome!.Ok);
        Assert.Equal(streamFailure, response.Payload is null);
    }

    [Fact]
    public void FullUnaryHighPlusContentFitsTheDataOnlyRequestAndResultFrames()
    {
        var bytes = Enumerable.Repeat((byte)0xfb, 16 * 1024 * 1024).ToArray();
        var base64 = Convert.ToBase64String(bytes);
        Assert.Contains('+', base64);
        var command = FileCommandParser.Parse("file.write", FileJson.Element(new { path = @"C:\file", contentBase64 = base64 }));
        var admission = new FileWorkerAdmission("host", "windows_unsupported", 1, "policy", null, @"C:\", @"C:\uploads");
        using var request = new MemoryStream();
        FileWorkerProtocol.WriteJson(request, FilePacketKind.Request, new FileWorkerRequest(FileWorkerProtocol.Version, admission, command));
        Assert.InRange(request.Length, base64.Length, 25 * 1024 * 1024);
        request.Position = 0;
        var decoded = FileWorkerProtocol.Json<FileWorkerRequest>(FileWorkerProtocol.Read(request));
        Assert.Equal(base64, decoded.Command.ContentBase64);
        using var result = new MemoryStream();
        FileWorkerProtocol.WriteJson(result, FilePacketKind.Result,
            new FileOperationResult(FileJson.Element(new { ok = true, base64 }), new(true)));
        result.Position = 0;
        var returned = FileWorkerProtocol.Json<FileOperationResult>(FileWorkerProtocol.Read(result));
        Assert.Equal(bytes, Convert.FromBase64String(returned.Payload.GetProperty("base64").GetString()!));
    }

    [Fact]
    public void UnaryCapsDoNotBecomeBinaryOrCreateHardCaps()
    {
        var fetch = FileCommandParser.Parse("file.fetch", FileJson.Element(new { path = @"C:\file", maxBytes = 100_000_000 }));
        Assert.Equal(16 * 1024 * 1024, fetch.MaxBytes);
        var binary = FileCommandParser.Parse("file.fetch", FileJson.Element(new { path = @"C:\file", maxBytes = 100_000_000, transport = "binary" }));
        Assert.Equal(100_000_000, binary.MaxBytes);
        Assert.Throws<FileCommandException>(() => FileCommandParser.Parse("file.fetch", FileJson.Element(new { path = @"C:\file", transport = "binary" })));
        var create = FileCommandParser.Parse("file.create", FileJson.Element(new { path = @"C:\file", sizeBytes = 100_000_000, maxBytes = 100_000_000, expectedSha256 = new string('a', 64) }));
        Assert.Equal(100_000_000, create.MaxBytes);
    }

    [Theory]
    [InlineData("{\"kind\":\"existing\",\"device\":\"17\",\"inode\":\"23\"}", true)]
    [InlineData("{\"kind\":\"existing\",\"device\":17,\"inode\":\"23\"}", false)]
    [InlineData("{\"kind\":\"write\",\"anchorPath\":\"C:\\\\Fixture\",\"anchorDevice\":\"17\",\"anchorInode\":\"20\"}", true)]
    [InlineData("{\"kind\":\"write\",\"anchorPath\":\"C:\\\\Fixture\",\"anchorDevice\":\"17\",\"anchorInode\":\"20\",\"targetDevice\":\"17\",\"targetInode\":\"23\"}", true)]
    [InlineData("{\"kind\":\"write\",\"anchorPath\":\"C:\\\\Fixture\",\"anchorDevice\":\"17\",\"anchorInode\":\"20\",\"targetDevice\":\"17\"}", false)]
    public void BindingsCarryLosslessStringsAndPairedTargetIdentity(string binding, bool valid)
    {
        using var args = JsonDocument.Parse("{\"path\":\"C:\\\\Fixture\\\\file\",\"expectedBinding\":" + binding + "}");
        if (valid) Assert.NotNull(FileCommandParser.Parse("file.stat", args.RootElement).ExpectedBinding);
        else Assert.Throws<FileCommandException>(() => FileCommandParser.Parse("file.stat", args.RootElement));
    }

    [Theory]
    [InlineData("file.fetch", true, false)]
    [InlineData("dir.list", true, false)]
    [InlineData("file.create", true, false)]
    [InlineData("fs.listDir", false, true)]
    [InlineData("terminal.upload", false, true)]
    public void TypedSemanticFailurePreservesPluginVersusAdminOuterEnvelope(string command, bool outerOk, bool outerError)
    {
        var result = new FileOperationResult(FileJson.Element(new { ok = false, code = "NOT_FOUND", message = "File not found" }),
            new(false, "NOT_FOUND", "File not found"));
        var response = FileCapability.Shape(new() { Id = "invoke-1", Command = command }, result);
        Assert.Equal(outerOk, response.Ok);
        Assert.False(response.FileOutcome!.Ok);
        Assert.Equal(outerError, response.Error is not null);
        Assert.Equal(outerError, response.Payload is null);
    }

    [Fact]
    public void PacketHeaderIsLiteralVersionedAndBoundedBeforePayloadAllocation()
    {
        using var stream = new MemoryStream();
        FileWorkerProtocol.Write(stream, FilePacketKind.Message, "abc"u8);
        Assert.Equal("4F4346310203000000616263", Convert.ToHexString(stream.ToArray()));
        using var oversized = new MemoryStream(Convert.FromHexString("4F4346310201001000"));
        Assert.Throws<InvalidDataException>(() => FileWorkerProtocol.Read(oversized));
        using var invalid = new MemoryStream(Convert.FromHexString("4F4346320200000000"));
        Assert.Throws<InvalidDataException>(() => FileWorkerProtocol.Read(invalid));
    }

    [Fact]
    public async Task AsyncPacketsPreserveEmptyEofVersusListener()
    {
        using var stream = new MemoryStream();
        await FileWorkerProtocol.WriteAsync(stream, new(FilePacketKind.Listen, []), CancellationToken.None);
        await FileWorkerProtocol.WriteAsync(stream, new(FilePacketKind.Message, []), CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(FilePacketKind.Listen, (await FileWorkerProtocol.ReadAsync(stream, CancellationToken.None)).Kind);
        Assert.Equal(FilePacketKind.Message, (await FileWorkerProtocol.ReadAsync(stream, CancellationToken.None)).Kind);
    }

    [Fact]
    public void AdmissionUsesExecutorResourcesNotBootstrapUnionOrMutableSettings()
    {
        var settings = new SettingsData { SandboxUploadStagingPath = @"C:\uploads", SandboxMaxTransferBytes = 7 * 1024 * 1024 };
        var policy = new NodeSandboxPolicyStore(settings).Capture();
        settings.SandboxUploadStagingPath = @"C:\changed";
        settings.SandboxMaxTransferBytes = 900 * 1024 * 1024;
        var resources = new NodeSandboxResources(new([@"C:\read"], [@"C:\write"], [@"C:\write\deny"], [@"C:\protected"]),
            new([@"C:\worker"], [@"C:\scratch"]));
        var io = new NodeSandboxProcessIo(null, null, null, Task.FromResult(0), NodeToolExecutionMode.Sandbox, policy, resources);
        var admission = FileOperations.CreateAdmission(io, null);
        Assert.Equal(@"C:\uploads", admission.UploadRoot);
        Assert.Equal(7 * 1024 * 1024, admission.TransferBytes);
        Assert.Throws<FileCommandException>(() => admission.DataAccess!.Demand(@"C:\worker\coreclr.dll", false));
        Assert.Throws<FileCommandException>(() => admission.DataAccess!.Demand(@"C:\scratch\file", true));
        admission.DataAccess!.Demand(@"C:\read\file", false);
        Assert.Throws<FileCommandException>(() => admission.DataAccess!.Demand(@"C:\read\file", true));
        var host = FileOperations.CreateAdmission(io with { ExecutionMode = NodeToolExecutionMode.Host, Resources = null }, null);
        Assert.Null(host.DataAccess);
        Assert.Equal("windows_unsupported", host.Reason);
        Assert.Throws<IOException>(() => FileOperations.CreateAdmission(io with { Resources = null }, null));
    }
}
