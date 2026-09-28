using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenClaw.BrowserBootstrap.Contracts;

public sealed class ContractException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public sealed record NativeContext(string NodePath, string CliPath, string StateDir, string ConfigPath, string BrowserProfile);
public sealed record ManagementRequest(int V, string Action, string Mode, NativeContext? Context, string[] ExpectedOrigins, string Store);
public sealed record Installation(string Generation, string ManifestPath, string LauncherPath, string BindingPath, string ReceiptPath);
public sealed record ManagementResponse(int V, bool Ok, string Code, string? Registration, string? Mode, string? Store, Installation? Installation)
{
    public static ManagementResponse Failure(string code) => new(1, false, code, null, null, null, null);
}
public sealed record HostBinding(int Version, string Mode, string ManifestPath, string[] ExpectedOrigins, NativeContext? NativeWindows);
public sealed record HostReceipt(string Owner, int Version, string Generation, string OwnerSid, bool TransportVerified,
    string ExecutableSha256, string BindingSha256, string ManifestSha256);
public sealed record HostManifest(string Name, string Description, string Path, string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("allowed_origins")] string[] AllowedOrigins);

/// <summary>Private Windows management contract, not the Chrome native v1 protocol or user configuration.</summary>
public static class ManagementContract
{
    public const int Limit = 32768;
    public const string Companion = "companion-managed-wsl", Native = "native-windows-cli";
    public const string Origin = "chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/";
    public const string HostName = "ai.openclaw.browser_bootstrap";
    public const string Description = "OpenClaw browser extension bootstrap";
    public const string ExeName = "OpenClaw.BrowserBootstrap.exe";
    public const string BindingName = "OpenClaw.BrowserBootstrap.binding.json";
    public const string ReceiptName = "OpenClaw.BrowserBootstrap.owned.json";
    public const string ManifestName = HostName + ".json";
    public const string Owner = "openclaw-browser-native-host";
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Codes = ["ok", "invalid_request", "context_conflict", "foreign_registration", "unsafe_path",
        "unsafe_acl", "binding_invalid", "browser_control_disabled", "transport_failed", "busy", "cancelled", "io_error", "platform_unsupported"];

    public static JsonDocument Document(byte[] bytes)
    {
        try
        {
            Require(bytes.Length is > 0 and <= Limit);
            var text = Utf8.GetString(bytes);
            Require(text[0] != '\uFEFF');
            CheckEscapedSurrogates(text);
            var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            try { Walk(doc.RootElement); Require(doc.RootElement.ValueKind == JsonValueKind.Object); }
            catch { doc.Dispose(); throw; }
            return doc;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or FormatException)
        { throw new ContractException("invalid_request"); }
    }
    private static void CheckEscapedSurrogates(string json)
    {
        // JsonDocument replaces unpaired escaped surrogates in GetString. Reject those before decoding.
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] != '\\') continue;
            if (++i >= json.Length) throw new ContractException("invalid_request");
            if (json[i] != 'u') continue;
            Require(i + 4 < json.Length && ushort.TryParse(json.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _));
            var unit = ushort.Parse(json.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            i += 4;
            if (unit is >= 0xdc00 and <= 0xdfff) throw new ContractException("invalid_request");
            if (unit is < 0xd800 or > 0xdbff) continue;
            Require(i + 6 < json.Length && json[i + 1] == '\\' && json[i + 2] == 'u' &&
                ushort.TryParse(json.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low) && low is >= 0xdc00 and <= 0xdfff);
            i += 6;
        }
    }
    private static void Walk(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject()) { Require(names.Add(p.Name)); Walk(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Walk(item);
    }
    public static void Fields(JsonElement root, params string[] names) => Require(root.ValueKind == JsonValueKind.Object &&
        root.EnumerateObject().Count() == names.Length && names.All(n => root.TryGetProperty(n, out _)));
    public static string String(JsonElement root, string name)
    {
        var e = root.GetProperty(name); Require(e.ValueKind == JsonValueKind.String); return e.GetString()!;
    }
    public static void Version(JsonElement root, string name) => Require(root.GetProperty(name).GetRawText() == "1");
    public static string RejectionOrigin(IEnumerable<string> allowed)
    {
        var set=allowed.ToHashSet(StringComparer.Ordinal);
        for(var i=0;i<=32;i++)
        {
            var id=new string('a',30)+(char)('a'+i/16)+(char)('a'+i%16);
            var origin="chrome-extension://"+id+"/";
            if(!set.Contains(origin))return origin;
        }
        throw new ContractException("invalid_request");
    }
    public static bool IsMode(string? mode) => mode is Companion or Native;
    public static bool IsProfile(string s) => s.Length is >= 1 and <= 64 &&
        (s[0] is >= 'a' and <= 'z' or >= '0' and <= '9') && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    public static bool IsGuid(string s) => s.Length == 36 && Guid.TryParseExact(s, "D", out var g) && g != Guid.Empty && g.ToString("D") == s;
    public static bool IsHash(string s) => s.Length == 64 && s.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    public static bool IsSid(string s)
    {
        var parts = s.Split('-');
        if (s.Length > 184 || parts.Length is < 4 or > 18 || parts[0] != "S" || parts[1] != "1") return false;
        for (var i = 2; i < parts.Length; i++)
            if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0') || !parts[i].All(char.IsAsciiDigit) ||
                !ulong.TryParse(parts[i], out var n) || n > (i == 2 ? 281474976710655UL : uint.MaxValue)) return false;
        return true;
    }
    public static bool PathEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (Fold(a[i]) != Fold(b[i])) return false;
        return true;
    }
    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
    private static bool EcmaSpace(char c) => c is >= '\u0009' and <= '\u000d' or '\u0020' or '\u00a0' or '\u1680' or
        >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
    public static bool IsPath(string s)
    {
        if (s.Length is < 3 or > 4096 || !char.IsAsciiLetter(s[0]) || s[1] != ':' || s[2] != '\\' || s.Contains('/')) return false;
        if (s.Length == 3) return true;
        foreach (var part in s[3..].Split('\\'))
        {
            if (part.Length == 0 || part is "." or ".." || EcmaSpace(part[0]) || EcmaSpace(part[^1]) || part[^1] == '.' ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) return false;
            for (var i = 0; i < part.Length; i++)
                if (char.IsSurrogate(part[i]) && (!char.IsHighSurrogate(part[i]) || ++i >= part.Length || !char.IsLowSurrogate(part[i]))) return false;
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))) return false;
        }
        return true;
    }
    public static NativeContext Context(JsonElement e)
    {
        Fields(e, "nodePath", "cliPath", "stateDir", "configPath", "browserProfile");
        var c = new NativeContext(String(e,"nodePath"), String(e,"cliPath"), String(e,"stateDir"), String(e,"configPath"), String(e,"browserProfile"));
        Require(new[]{c.NodePath,c.CliPath,c.StateDir,c.ConfigPath}.All(IsPath) && IsProfile(c.BrowserProfile));
        Require(PathEquals(c.NodePath.Split('\\')[^1], "node.exe") && PathEquals(c.CliPath.Split('\\')[^1], "openclaw.mjs"));
        return c;
    }
    public static string[] Origins(JsonElement e, string mode)
    {
        Require(e.ValueKind == JsonValueKind.Array);
        var a = e.EnumerateArray().Select(x => { Require(x.ValueKind == JsonValueKind.String); return x.GetString()!; }).ToArray();
        Require(a.Length is >= 1 and <= 32 && a.Contains(Origin));
        for (var i = 0; i < a.Length; i++)
            Require(a[i].Length == 52 && a[i].StartsWith("chrome-extension://", StringComparison.Ordinal) && a[i][^1] == '/' &&
                a[i].AsSpan(19,32).ToArray().All(c => c is >= 'a' and <= 'p') && (i == 0 || string.CompareOrdinal(a[i-1],a[i]) < 0));
        Require(mode != Companion || (a.Length == 1 && a[0] == Origin));
        return a;
    }
    public static ManagementRequest ParseRequest(byte[] bytes)
    {
        using var doc = Document(bytes); var r = doc.RootElement;
        Fields(r,"v","action","mode","context","expectedOrigins","store"); Version(r,"v");
        var mode = String(r,"mode"); var action = String(r,"action"); var store = String(r,"store");
        Require(IsMode(mode) && (action switch { "inspect" => store == "preserve", "install" => store is "preserve" or "request",
            "uninstall" => store is "preserve" or "remove", _ => false }));
        var ctx = r.GetProperty("context"); Require(mode != Companion || ctx.ValueKind == JsonValueKind.Null);
        return new(1, action, mode, mode == Companion ? null : Context(ctx), Origins(r.GetProperty("expectedOrigins"),mode), store);
    }
    public static HostBinding ParseBinding(byte[] bytes)
    {
        using var doc = Document(bytes); var r = doc.RootElement;
        Fields(r,"version","mode","manifestPath","expectedOrigins","nativeWindows"); Version(r,"version");
        var mode=String(r,"mode"); Require(IsMode(mode)); var ctx=r.GetProperty("nativeWindows");
        Require(mode != Companion || ctx.ValueKind == JsonValueKind.Null); var manifest=String(r,"manifestPath"); Require(IsPath(manifest));
        return new(1,mode,manifest,Origins(r.GetProperty("expectedOrigins"),mode),mode==Companion?null:Context(ctx));
    }
    public static HostReceipt ParseReceipt(byte[] bytes)
    {
        using var doc=Document(bytes);var r=doc.RootElement;
        Fields(r,"owner","version","generation","ownerSid","transportVerified","executableSha256","bindingSha256","manifestSha256"); Version(r,"version");
        Require(String(r,"owner")==Owner && r.GetProperty("transportVerified").ValueKind==JsonValueKind.True);
        var value=new HostReceipt(Owner,1,String(r,"generation"),String(r,"ownerSid"),true,String(r,"executableSha256"),String(r,"bindingSha256"),String(r,"manifestSha256"));
        Require(IsGuid(value.Generation)&&IsSid(value.OwnerSid)&&IsHash(value.ExecutableSha256)&&IsHash(value.BindingSha256)&&IsHash(value.ManifestSha256));return value;
    }
    public static HostManifest ParseManifest(byte[] bytes)
    {
        using var doc=Document(bytes);var r=doc.RootElement;Fields(r,"name","description","path","type","allowed_origins");
        Require(String(r,"name")==HostName && String(r,"description")==Description && String(r,"type")=="stdio" && IsPath(String(r,"path")));
        return new(HostName,Description,String(r,"path"),"stdio",Origins(r.GetProperty("allowed_origins"),Native));
    }
    public static bool SameOwnership(ManagementRequest r, HostBinding b) => r.Mode==b.Mode && (r.Mode==Companion ||
        (r.Context is {} a && b.NativeWindows is {} c && PathEquals(a.StateDir,c.StateDir)&&PathEquals(a.ConfigPath,c.ConfigPath)&&a.BrowserProfile==c.BrowserProfile));
    public static bool Matches(ManagementRequest r, HostBinding b) => SameOwnership(r,b) && r.ExpectedOrigins.SequenceEqual(b.ExpectedOrigins) &&
        (r.Mode==Companion || (PathEquals(r.Context!.NodePath,b.NativeWindows!.NodePath)&&PathEquals(r.Context.CliPath,b.NativeWindows.CliPath)));
    public static void ValidateMetadata(Installation d,string sid,byte[] bindingBytes,byte[] receiptBytes,byte[] manifestBytes,byte[] executable)
    {
        try
        {
            var b=ParseBinding(bindingBytes);var r=ParseReceipt(receiptBytes);var m=ParseManifest(manifestBytes);
            Require(IsGuid(d.Generation)&&r.Generation==d.Generation&&r.OwnerSid==sid);
            ValidateResponse(new(1,true,"ok","owned",b.Mode,"missing",d));
            Require(b.ManifestPath==d.ManifestPath&&m.Path==d.LauncherPath&&b.ExpectedOrigins.SequenceEqual(m.AllowedOrigins));
            string Hash(byte[] x)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(x));
            Require(r.ExecutableSha256==Hash(executable)&&r.BindingSha256==Hash(bindingBytes)&&r.ManifestSha256==Hash(manifestBytes));
        }
        catch(ContractException){throw new ContractException("binding_invalid");}
    }
    public static void ValidateFileNames(IEnumerable<string> names)
    {
        var expected=new[]{ExeName,BindingName,ReceiptName,ManifestName}.Order(StringComparer.Ordinal);
        if(!names.Order(StringComparer.Ordinal).SequenceEqual(expected))throw new ContractException("binding_invalid");
    }
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value,Json);
    public static byte[] ResponseBytes(ManagementResponse r)
    {
        ValidateResponse(r); var data=Serialize(r); Require(data.Length<Limit);return [..data,(byte)'\n'];
    }
    public static void ValidateResponse(ManagementResponse r)
    {
        Require(r.V==1 && Codes.Contains(r.Code) && r.Ok==(r.Code=="ok") && r.Registration is null or "missing" or "owned" or "foreign" or "invalid" &&
            r.Store is null or "missing" or "requested" or "foreign" or "invalid");
        Require(r.Registration=="owned" ? IsMode(r.Mode) : r.Mode is null && r.Installation is null);
        if(r.Code is "invalid_request" or "platform_unsupported" or "busy") Require(r.Registration is null && r.Mode is null && r.Store is null && r.Installation is null);
        if(r.Code=="context_conflict") Require(r.Installation is null);
        if(r.Installation is {} d)
        {
            Require(IsGuid(d.Generation)&&new[]{d.ManifestPath,d.LauncherPath,d.BindingPath,d.ReceiptPath}.All(IsPath));
            var dir=d.ManifestPath[..(d.ManifestPath.LastIndexOf('\\')+1)];
            Require(dir.EndsWith("\\generations\\"+d.Generation+"\\",StringComparison.Ordinal) && d.ManifestPath==dir+ManifestName &&
                d.LauncherPath==dir+ExeName && d.BindingPath==dir+BindingName && d.ReceiptPath==dir+ReceiptName);
        }
    }
    public static ManagementResponse ParseResponse(byte[] bytes,int exitCode)
    {
        Require(bytes.Length is > 1 and <= Limit && bytes[^1]=='\n' && bytes[^2]=='}' && bytes[0]=='{' && !bytes.AsSpan(0,bytes.Length-1).Contains((byte)'\n') && !bytes.Contains((byte)'\r'));
        var inString=false;var escaped=false;
        foreach(var value in bytes[..^1])
        {
            if(inString){if(escaped)escaped=false;else if(value=='\\')escaped=true;else if(value=='"')inString=false;}
            else{if(value=='"')inString=true;else Require(value is not (9 or 10 or 13 or 32));}
        }
        using var doc=Document(bytes[..^1]);var r=doc.RootElement;
        Fields(r,"v","ok","code","registration","mode","store","installation");Version(r,"v");Require(r.GetProperty("ok").ValueKind is JsonValueKind.True or JsonValueKind.False);
        string? Nullable(string n) => r.GetProperty(n).ValueKind==JsonValueKind.Null?null:String(r,n);
        Installation? d=null;var i=r.GetProperty("installation");
        if(i.ValueKind!=JsonValueKind.Null){Fields(i,"generation","manifestPath","launcherPath","bindingPath","receiptPath");d=new(String(i,"generation"),String(i,"manifestPath"),String(i,"launcherPath"),String(i,"bindingPath"),String(i,"receiptPath"));}
        var result=new ManagementResponse(1,r.GetProperty("ok").GetBoolean(),String(r,"code"),Nullable("registration"),Nullable("mode"),Nullable("store"),d);
        ValidateResponse(result);Require(exitCode==(result.Ok?0:1));return result;
    }
    public static void Require(bool condition) { if(!condition)throw new ContractException("invalid_request"); }
}
