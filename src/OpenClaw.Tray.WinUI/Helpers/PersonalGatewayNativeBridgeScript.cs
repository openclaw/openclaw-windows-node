using System.Text.Json;

namespace OpenClawTray.Helpers;

internal static class PersonalGatewayNativeBridgeScript
{
    private const string BridgeScript = """
        (() => {
          if (!window.chrome?.webview) return;
          const bridge = window.OpenClawNativeGatewayAuth || {};
          bridge.postMessage = (message) => window.chrome.webview.postMessage(
            typeof message === "string" ? message : JSON.stringify(message));
          bridge.onmessage = bridge.onmessage || null;
          Object.defineProperty(window, "OpenClawNativeGatewayAuth", {
            configurable: true, value: bridge
          });
          window.chrome.webview.addEventListener("message", (event) => {
            const handler = window.OpenClawNativeGatewayAuth;
            if (typeof handler?.onmessage === "function") {
              handler.onmessage({ data: typeof event.data === "string"
                ? event.data : JSON.stringify(event.data) });
            }
          });
        })();
        """;

    internal static string Create(string gatewayUrl)
    {
        var nativeControlAuth = JsonSerializer.Serialize(new
        {
            gatewayUrl,
            nativeConnectAuth = true
        });

        return $"window.__OPENCLAW_NATIVE_CONTROL_AUTH__ = {nativeControlAuth};\n{BridgeScript}";
    }
}
