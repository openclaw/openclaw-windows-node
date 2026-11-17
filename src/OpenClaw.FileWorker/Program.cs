using System.Text.Json;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.FileWorker;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args is not ["--stdio"]) return 64;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        try
        {
            var packet = FileWorkerProtocol.Read(input);
            if (packet.Kind != FilePacketKind.Request) return 65;
            var request = FileWorkerProtocol.Json<FileWorkerRequest>(packet);
            if (request.Version != FileWorkerProtocol.Version) return 65;
            var result = new FileWorkerEngine().Execute(request.Admission, request.Command, new PipeMessages(input, output));
            FileWorkerProtocol.WriteJson(output, FilePacketKind.Result, result);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FileCommandException or ArgumentException)
        {
            // Protocol diagnostics never include request JSON, file bytes, paths, or credentials.
            Console.Error.WriteLine("The fixed file worker rejected its internal protocol.");
            return 65;
        }
    }

    private sealed class PipeMessages(Stream input, Stream output) : IWorkerMessages
    {
        private bool _listening;
        public void Listen()
        {
            if (_listening) throw new InvalidDataException("A worker may register only one listener.");
            _listening = true;
            FileWorkerProtocol.Write(output, FilePacketKind.Listen, []);
        }
        public byte[] Receive()
        {
            if (!_listening) throw new InvalidDataException("No file listener.");
            var packet = FileWorkerProtocol.Read(input);
            if (packet.Kind != FilePacketKind.Message) throw new InvalidDataException("Expected a file message.");
            return packet.Bytes;
        }
        public void Send(ReadOnlySpan<byte> message)
        {
            if (!_listening) throw new InvalidDataException("No file listener.");
            FileWorkerProtocol.Write(output, FilePacketKind.Message, message);
        }
    }
}
