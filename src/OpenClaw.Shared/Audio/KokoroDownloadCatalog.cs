// Pinned Kokoro downloads shared by the app and the Inno uninstall helper.
// Keep this file compatible with Windows PowerShell 5.1 Add-Type (.NET Framework).
// Example: KokoroDownloadCatalog.EnglishModel describes one verified download.
namespace OpenClaw.Shared.Audio
{
    internal sealed class KokoroDownloadAsset
    {
        internal readonly string PackId, FileName, Url, Sha256;
        internal readonly long SizeBytes;

        internal KokoroDownloadAsset(string packId, string fileName, string url, string sha256, long sizeBytes)
        {
            PackId = packId; FileName = fileName; Url = url; Sha256 = sha256; SizeBytes = sizeBytes;
        }
    }

    internal static class KokoroDownloadCatalog
    {
        private const string Release = "https://github.com/larroy/kokoro.cpp/releases/download/voices_model_files/";
        internal static readonly KokoroDownloadAsset EnglishModel = new KokoroDownloadAsset(
            "v1.1-en-zh", "kokoro-v1.1-zh.onnx", Release + "kokoro-v1.1-zh.onnx",
            "eefec708cbc7aba8e8129b5c2f7cb92e1fe7d281af1e1dd451592d9ff0714a0d", 343605188);
        internal static readonly KokoroDownloadAsset EnglishVoices = new KokoroDownloadAsset(
            "v1.1-en-zh", "voices-v1.1-zh.bin", Release + "voices-v1.1-zh.bin",
            "e678019845e6cfe3b7c34531779396b28f509451b91e6535d5dc09bbf11a4be5", 53792177);
        internal static readonly KokoroDownloadAsset SpanishModel = new KokoroDownloadAsset(
            "v1.0-es", "kokoro-v1.0.onnx",
            "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx",
            "7d5df8ecf7d4b1878015a32686053fd0eebe2bc377234608764cc0ef3636a6c5", 325532387);
        internal static readonly KokoroDownloadAsset SpanishVoices = new KokoroDownloadAsset(
            "v1.0-es", "voices-v1.0-es.bin", Release + "voices-v1.0-es.bin",
            "cdaf0ecca101f3738763f6e3a13b63d46137909b8ebcf9de2be4ef2a73014c52", 1566778);

        // Stable bit positions identify exactly the files shown in the uninstall preview.
        internal static readonly KokoroDownloadAsset[] All =
            { EnglishModel, EnglishVoices, SpanishModel, SpanishVoices };
    }
}
