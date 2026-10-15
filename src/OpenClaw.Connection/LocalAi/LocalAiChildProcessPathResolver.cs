using OpenClaw.Shared.IO;

namespace OpenClaw.Connection.LocalAi;

// Keep the Local AI launch seam while sharing handle-based resolution with diagnostics.
internal static class LocalAiChildProcessPathResolver
{
    public static string Resolve(string path) => WindowsExistingPathResolver.Resolve(path);
}
