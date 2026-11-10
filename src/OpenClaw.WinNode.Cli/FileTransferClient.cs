using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.WinNode.Cli;

internal static class FileTransferClient
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    internal static async Task<int> RunAsync(HttpClient http, Uri endpoint, WinNodeOptions options,
        JsonElement arguments, TextWriter stdout, TextWriter stderr)
    {
        using var lifetime = new CancellationTokenSource(options.InvokeTimeoutMs);
        var ct = lifetime.Token;
        FileStream? localFile = null;
        string? partial = null, lease = null;
        Task? poll = null;
        Task<JsonElement>? invocation = null;
        Task? transfer = null;
        Exception? invocationFailure = null;
        FileFrameChannel? channel = null;
        try
        {
            var args = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            var create = options.InputFile != null;
            if (args["preflightOnly"]?.GetValue<bool>() == true)
                throw new ArgumentException("Local file options cannot be combined with preflightOnly.");
            if (create)
            {
                localFile = new FileStream(options.InputFile!, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileLimits.MessageBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var max = args["maxBytes"]?.GetValue<long>() ?? FileLimits.DefaultCreateBytes;
                if (max < 0 || max > FileLimits.SafeInteger || localFile.Length > max)
                    throw new ArgumentException("Input file exceeds maxBytes.");
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(localFile, ct));
                if (args["sizeBytes"] != null && args["sizeBytes"]!.GetValue<long>() != localFile.Length)
                    throw new ArgumentException("Input file does not match sizeBytes.");
                if (args["expectedSha256"] != null &&
                    !string.Equals(args["expectedSha256"]!.GetValue<string>(), hash, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Input file does not match expectedSha256.");
                args["sizeBytes"] ??= JsonValue.Create(localFile.Length);
                args["expectedSha256"] ??= JsonValue.Create(hash);
                localFile.Position = 0;
            }
            else
            {
                if (args["transport"] != null && args["transport"]!.GetValue<string>() != "binary")
                    throw new ArgumentException("Output file requires binary transport.");
                args["transport"] = "binary";
                var output = Path.GetFullPath(options.OutputFile!);
                if (File.Exists(output) || Directory.Exists(output))
                    throw new IOException("Output already exists. File output never overwrites.");
                var candidate = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.openclaw-{Guid.NewGuid():N}.partial");
                localFile = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    FileLimits.MessageBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                partial = candidate;
            }

            // Parse before admission, including explicit positive maxBytes for binary fetch.
            FileCommandRequest parsed;
            try { parsed = FileCommandParser.Parse(options.Command!, JsonSerializer.SerializeToElement(args)); }
            catch (FileCommandException ex) { throw new ArgumentException(ex.Message, ex); }
            var preflight = args.DeepClone().AsObject();
            preflight["preflightOnly"] = true;
            var checkedPath = Payload(await CallAsync(http, endpoint, "tools/call",
                new { name = options.Command, arguments = preflight }, options.Command, ct));
            if (args["expectedCanonicalPath"] == null)
                args["expectedCanonicalPath"] = checkedPath.GetProperty("path").GetString();
            if (args["expectedBinding"] == null)
                args["expectedBinding"] = JsonNode.Parse(checkedPath.GetProperty("binding").GetRawText());

            var requestId = Guid.NewGuid().ToString("N");
            var opened = await CallAsync(http, endpoint, "openclaw/files/open",
                new { v = 1, command = options.Command, requestId, timeoutMs = options.InvokeTimeoutMs }, null, ct);
            lease = opened.GetProperty("lease").GetString()!;
            long inputSeq = 0, cursor = 0, received = 0;
            var acknowledgments = Channel.CreateBounded<bool>(1);
            using var hashOutput = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            channel = new FileFrameChannel(async (frame, stop) =>
            {
                await CallAsync(http, endpoint, "openclaw/files/input",
                    new { v = 1, lease, seq = inputSeq++, payloadJSON = frame }, null, stop);
            }, ct, requireReady: true, announceReady: false);
            using var channelFailure = channel.Token.Register(() => lifetime.Cancel());
            await using var listener = await channel.ListenAsync(async (message, stop) =>
            {
                if (create)
                {
                    if (!message.Span.SequenceEqual("ack"u8) || !acknowledgments.Writer.TryWrite(true))
                        throw new InvalidDataException("Invalid create acknowledgment.");
                }
                else
                {
                    received += message.Length;
                    if (received > parsed.MaxBytes) throw new InvalidDataException("Fetch exceeds maxBytes.");
                    await localFile.WriteAsync(message, stop);
                    hashOutput.AppendData(message.Span);
                    await channel.SendAsync("ack"u8.ToArray(), stop);
                }
            }, ct);

            poll = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var result = await CallAsync(http, endpoint, "openclaw/files/poll", new { v = 1, lease, ack = cursor }, null, ct);
                        foreach (var frame in result.GetProperty("frames").EnumerateArray())
                        {
                            if (frame.GetProperty("seq").GetInt64() != cursor)
                                throw new InvalidDataException("File output cursor is out of order.");
                            channel.Receive(frame.GetProperty("payloadJSON").GetString()!);
                            cursor++;
                        }
                        await Task.Delay(PollInterval, ct);
                    }
                }
                catch { lifetime.Cancel(); throw; }
            }, CancellationToken.None);
            invocation = ObserveInvocationAsync(CallAsync(http, endpoint, "tools/call", new
            {
                name = options.Command, arguments = args,
                _meta = new Dictionary<string, object> { ["openclaw/file-channel"] = new { v = 1, lease } }
            }, options.Command, ct, requestId));
            transfer = Task.Run(async () =>
            {
                using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(ct, channel.Token);
                var stop = streamStop.Token;
                await channel.Ready.WaitAsync(stop);
                if (!create)
                {
                    await channel.SendAsync("start"u8.ToArray(), stop);
                    return;
                }
                var buffer = new byte[FileLimits.MessageBytes];
                int count;
                while ((count = await localFile.ReadAsync(buffer, stop)) != 0)
                {
                    await channel.SendAsync(buffer.AsMemory(0, count), stop);
                    await acknowledgments.Reader.ReadAsync(stop);
                }
                await channel.SendAsync(ReadOnlyMemory<byte>.Empty, stop);
            }, CancellationToken.None);
            if (await Task.WhenAny(invocation, transfer) == invocation)
                _ = Payload(await invocation);
            try { await transfer; }
            catch when (invocation.IsFaulted)
            {
                _ = await invocation;
                throw;
            }
            var completed = await invocation;
            var payload = Payload(completed);
            if (create)
            {
                var status = payload.GetProperty("status").GetString();
                if (status is not ("created" or "exists"))
                    throw new InvalidDataException("Create returned an unknown completion status.");
                if (status == "created" && (payload.GetProperty("size").GetInt64() != parsed.SizeBytes ||
                    payload.GetProperty("sha256").GetString() != parsed.ExpectedSha256))
                    throw new InvalidDataException("Create result does not match the input size and SHA256.");
            }
            if (!create)
            {
                var actualHash = Convert.ToHexStringLower(hashOutput.GetHashAndReset());
                if (payload.GetProperty("size").GetInt64() != received ||
                    payload.GetProperty("sha256").GetString() != actualHash ||
                    (parsed.ExpectedSha256 != null && parsed.ExpectedSha256 != actualHash))
                    throw new InvalidDataException("Fetch size or SHA256 does not match the received bytes.");
                await localFile.FlushAsync(ct);
                await localFile.DisposeAsync();
                localFile = null;
                File.Move(partial!, Path.GetFullPath(options.OutputFile!), overwrite: false);
                partial = null;
            }
            stdout.WriteLine(CliRunner.PrettyPrint(payload));
            return 0;

            async Task<JsonElement> ObserveInvocationAsync(Task<JsonElement> call)
            {
                try
                {
                    var result = await call;
                    _ = Payload(result);
                    return result;
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref invocationFailure, ex);
                    lifetime.Cancel();
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failure = Volatile.Read(ref invocationFailure) ?? ex;
            stderr.WriteLine(CliRunner.SanitizeForStderr(failure is OperationCanceledException
                ? "File transfer cancelled or timed out." : failure.Message, options.Verbose));
            return failure is ArgumentException ? 2 : 1;
        }
        finally
        {
            lifetime.Cancel();
            if (channel != null) await channel.DisposeAsync();
            foreach (var task in new Task?[] { poll, transfer, invocation })
                if (task != null) { try { await task; } catch { } }
            if (lease != null)
            {
                using var close = new CancellationTokenSource(CloseTimeout);
                try { await CallAsync(http, endpoint, "openclaw/files/close", new { v = 1, lease }, null, close.Token); }
                catch { }
            }
            if (localFile != null) await localFile.DisposeAsync();
            if (partial != null)
            {
                try { File.Delete(partial); }
                catch (IOException) { stderr.WriteLine("Unable to remove the owned partial output file."); }
            }
        }
    }

    private static JsonElement Payload(JsonElement result)
    {
        if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            throw new IOException(result.GetProperty("content")[0].GetProperty("text").GetString());
        using var doc = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        var payload = doc.RootElement;
        if (payload.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new IOException("File operation failed.");
        return payload.Clone();
    }

    private static async Task<JsonElement> CallAsync(HttpClient http, Uri endpoint, string method,
        object parameters, string? command, CancellationToken ct, string? requestId = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0", id = requestId ?? Guid.NewGuid().ToString("N"), method, @params = parameters
        }, FileTransportJson.Options);
        if (bytes.Length > FileTransportJson.EnvelopeBytes) throw new InvalidDataException("File request exceeds its byte limit.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (command != null) request.Headers.Add(FileTransportJson.EnvelopeHeader, command);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"File transport HTTP {(int)response.StatusCode}.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("error", out _)) throw new IOException("File transport JSON-RPC error.");
        return doc.RootElement.GetProperty("result").Clone();
    }
}
