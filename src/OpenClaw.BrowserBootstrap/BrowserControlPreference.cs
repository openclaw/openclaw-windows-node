using System.Text;
using System.Text.Json;

namespace OpenClaw.BrowserBootstrap;

internal static class BrowserControlPreference
{
    // Keep the existing persisted preference and its 1 MiB cap, not a new user configuration owner.
    public static bool Allows(byte[]? bytes)
    {
        if(bytes is null)return true;
        if(bytes.Length>1024*1024)return false;
        try
        {
            using var doc=JsonDocument.Parse(new UTF8Encoding(false,true).GetString(bytes));
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)return false;
            var values=doc.RootElement.EnumerateObject().Where(p=>string.Equals(p.Name,"NodeBrowserProxyEnabled",StringComparison.OrdinalIgnoreCase)).ToArray();
            return values.Length==0||values.Length==1&&values[0].Value.ValueKind==JsonValueKind.True;
        }
        catch(Exception e) when(e is JsonException or DecoderFallbackException){return false;}
    }
}
