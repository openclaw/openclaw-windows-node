using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenClaw.Shared.FileTransfer;

public static class FileCommandParser
{
    public static readonly IReadOnlyList<string> Commands =
        Array.AsReadOnly(new[] { "file.stat", "file.fetch", "dir.list", "dir.fetch", "file.write", "file.create", "fs.listDir", "terminal.upload" });

    public static FileCommandRequest Parse(string command, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw Invalid("Arguments must be an object.");
        var operation = command switch
        {
            "file.stat" => FileOperation.Stat, "file.fetch" => FileOperation.Fetch,
            "dir.list" => FileOperation.List, "dir.fetch" => FileOperation.Archive,
            "file.write" => FileOperation.Write, "file.create" => FileOperation.Create,
            "fs.listDir" => FileOperation.ListDirectories, "terminal.upload" => FileOperation.Upload,
            _ => throw Invalid("Unknown file command.")
        };
        if (operation == FileOperation.Upload)
        {
            var name = String(args, "name", required: true)!;
            if (name.Length is < 1 or > 255) throw Invalid("name must contain 1 to 255 characters.");
            var upload = String(args, "contentBase64", required: true)!;
            StrictFileBase64.Inspect(upload, FileLimits.UnaryBytes, standardPadded: true);
            return new(operation, "", ContentBase64: upload, Name: name);
        }
        var path = String(args, "path", operation != FileOperation.ListDirectories) ?? "";
        if (operation == FileOperation.ListDirectories) return new(operation, path);
        var preflight = Bool(args, "preflightOnly");
        FileBinding? binding = null;
        if (args.TryGetProperty("expectedBinding", out var value))
        {
            try { binding = value.Deserialize<FileBinding>(FileJson.Options) ?? throw Invalid("Invalid binding."); }
            catch (JsonException) { throw Invalid("Invalid binding."); }
            binding.Validate();
        }
        var transport = operation == FileOperation.Fetch ? String(args, "transport") : null;
        if (transport is not null && transport != "binary") throw Invalid("transport must be binary.");
        var binary = transport == "binary";
        long max = FileLimits.DefaultUnaryBytes;
        if (operation is FileOperation.Fetch or FileOperation.Archive)
        {
            max = Integer(args, "maxBytes", binary ? null : FileLimits.DefaultUnaryBytes, minimum: 1);
            if (!binary) max = Math.Min(max, FileLimits.UnaryBytes);
        }
        var size = operation == FileOperation.Create ? Integer(args, "sizeBytes", null) : 0;
        if (operation == FileOperation.Create)
        {
            max = Integer(args, "maxBytes", FileLimits.DefaultCreateBytes);
            if (size > max) throw Invalid("sizeBytes exceeds maxBytes.");
        }
        var hash = operation is FileOperation.Write or FileOperation.Create
            ? String(args, "expectedSha256", operation == FileOperation.Create) : null;
        if (hash is not null)
        {
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw Invalid("expectedSha256 must contain 64 hexadecimal digits.");
            hash = hash.ToLowerInvariant();
        }
        var content = operation == FileOperation.Write ? String(args, "contentBase64", required: true) : null;
        if (content is not null) StrictFileBase64.Inspect(content, FileLimits.UnaryBytes, standardPadded: false);
        var entries = operation == FileOperation.List
            ? (int)Math.Min(Integer(args, "maxEntries", FileLimits.DefaultPageEntries, 1), FileLimits.TreeEntries) : FileLimits.DefaultPageEntries;
        var token = operation == FileOperation.List ? String(args, "pageToken") : null;
        var offset = token is not null && token.Length > 0 && token.All(char.IsAsciiDigit) &&
            long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed <= FileLimits.SafeInteger ? parsed : 0;
        return new(operation, path, preflight, Bool(args, "followSymlinks"), String(args, "expectedCanonicalPath"),
            binding, operation == FileOperation.Fetch ? String(args, "rootPath") : null, max, binary, entries, offset, content,
            operation == FileOperation.Write && Bool(args, "overwrite"),
            operation is FileOperation.Write or FileOperation.Create && Bool(args, "createParents"),
            operation == FileOperation.Write && Bool(args, "rejectHardlinks"), hash, size);
    }

    private static FileCommandException Invalid(string message) => new("INVALID_PARAMS", message);
    private static string? String(JsonElement args, string name, bool required = false)
    {
        if (!args.TryGetProperty(name, out var value))
        {
            if (required) throw Invalid($"{name} is required.");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"{name} must be a string.");
        return value.GetString();
    }
    private static bool Bool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid($"{name} must be boolean.") };
    }
    private static long Integer(JsonElement args, string name, long? fallback, long minimum = 0)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback ?? throw Invalid($"{name} is required.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < minimum || number > FileLimits.SafeInteger)
            throw Invalid($"{name} must be a safe integer of at least {minimum}.");
        return number;
    }
}

public static class StrictFileBase64
{
    public static int Inspect(string value, int maximumBytes, bool standardPadded)
    {
        if (value.Length > ((long)maximumBytes + 2) / 3 * 4) throw new FileCommandException("FILE_TOO_LARGE", "Decoded content exceeds the byte limit.");
        var data = value.Length;
        while (data > 0 && value[data - 1] == '=') data--;
        var padding = value.Length - data;
        var remainder = data % 4;
        if (padding > 2 || remainder == 1 || (padding == 1 && remainder != 3) || (padding == 2 && remainder != 2) ||
            (standardPadded && value.Length % 4 != 0)) throw Invalid();
        for (var i = 0; i < data; i++)
            if (Digit(value[i], standardPadded) < 0) throw Invalid();
        if (data > 0 && ((remainder == 2 && (Digit(value[data - 1], standardPadded) & 15) != 0) ||
            (remainder == 3 && (Digit(value[data - 1], standardPadded) & 3) != 0))) throw Invalid();
        var length = (int)((long)data * 3 / 4);
        if (length > maximumBytes) throw new FileCommandException("FILE_TOO_LARGE", "Decoded content exceeds the byte limit.");
        return length;
    }

    public static byte[] Decode(string value, bool standardPadded = false)
    {
        Inspect(value, FileLimits.UnaryBytes, standardPadded);
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static FileCommandException Invalid() => new("INVALID_BASE64", "Content must use canonical base64.");
    private static int Digit(char c, bool standard) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A', >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52, '+' => 62, '/' => 63,
        '-' when !standard => 62, '_' when !standard => 63, _ => -1
    };
}
