
using System.Text.Json;

namespace OpenClaw.Shared.Dashboards;

/// <summary>A validated message from the widget relay page to native code.</summary>
public abstract record BoardWidgetHostMessage
{
    /// <summary>The relay page loaded and waits for <c>init</c>.</summary>
    public sealed record PageReady : BoardWidgetHostMessage;

    /// <summary>The sandbox proxy finished its handshake for <paramref name="SandboxUrl"/>.</summary>
    public sealed record ProxyReady(string SandboxUrl) : BoardWidgetHostMessage;

    /// <summary>The proxy rendered the document delivered with <paramref name="RenderId"/>.</summary>
    public sealed record ResourceLoaded(string RenderId) : BoardWidgetHostMessage;

    /// <summary>The widget wrapper reported its content height in CSS pixels.</summary>
    public sealed record ContentSize(double Height) : BoardWidgetHostMessage;

    /// <summary>The proxy frame failed to load.</summary>
    public sealed record FrameError : BoardWidgetHostMessage;
}

/// <summary>
/// The trusted relay page that replaces the upstream Control UI document in a
/// widget WebView2. It embeds the Gateway sandbox proxy, accepts messages only
/// from that frame and origin, and relays a narrow protocol to native code.
/// The page is served from memory for <see cref="PageUrl"/>; it has no
/// authority of its own.
/// </summary>
public static class BoardWidgetHostPage
{
    /// <summary>Non-resolvable host owned by the app's request interception.</summary>
    public const string HostName = "openclaw-board-host.invalid";
    public const string Origin = "http://" + HostName;
    public const string PageUrl = Origin + "/widget-host.html";

    /// <summary>Upper bound for a reported content height (upstream caps at 240 grid rows).</summary>
    public const double MaxContentHeight = 240 * (BoardGridLayout.RowHeight + BoardGridLayout.Gap);

    /// <summary>Largest relay message accepted from the page.</summary>
    public const int MaxMessageChars = 64 * 1024;

    public static Uri PageUri { get; } = new(PageUrl);

    /// <summary>
    /// Content-Security-Policy for the relay page: only its own nonce script,
    /// only the widget's sandbox origin as a frame, no network or other resources.
    /// </summary>
    public static string ContentSecurityPolicy(string nonce, string sandboxOrigin) =>
        $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; frame-src {sandboxOrigin}; " +
        "base-uri 'none'; form-action 'none'; object-src 'none'";

    /// <summary>Relay page HTML. Configuration arrives later in the native <c>init</c> message.</summary>
    public static string BuildHtml(string nonce) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="referrer" content="origin">
        <style>html,body{margin:0;height:100%;overflow:hidden;background:transparent}
        iframe{display:block;border:0;background:transparent;width:100%;height:100%;transform-origin:0 0}</style>
        </head><body>
        <script nonce="{{nonce}}">
        (() => {
          const webview = window.chrome && window.chrome.webview;
          if (!webview || window.top !== window) return;
          const send = (channel, data) => webview.postMessage({ channel, data });
          let frame = null, sandboxOrigin = "";
          // Chromium paints an opaque backdrop when a frame element's color scheme differs
          // from its document's. Track the widget theme on the element; the page stays
          // transparent so native card chrome shows through.
          const setScheme = (mode) => { if (frame && (mode === "light" || mode === "dark")) frame.style.colorScheme = mode; };
          // Previews render the full-size widget scaled down; resizing only changes the transform.
          const setScale = (value) => {
            if (!frame) return;
            const scale = Number(value);
            const mini = Number.isFinite(scale) && scale > 0 && scale < 1;
            frame.style.width = mini ? (100 / scale) + "%" : "";
            frame.style.height = mini ? (100 / scale) + "%" : "";
            frame.style.transform = mini ? "scale(" + scale + ")" : "";
          };
          window.addEventListener("message", (event) => {
            if (!frame || event.source !== frame.contentWindow || event.origin !== sandboxOrigin) return;
            // Widgets get no host bridge authority: close every offered private port.
            if (event.ports) for (const offered of event.ports) offered.close();
            const data = event.data;
            if (data && (data.type === "openclaw:widget-bridge-port-offer" || data.type === "openclaw:widget-prompt-offer")) return;
            send("sandbox", data);
          });
          webview.addEventListener("message", (event) => {
            const message = event.data;
            if (!message || typeof message !== "object") return;
            if (message.channel === "init" && !frame) {
              sandboxOrigin = String(message.sandboxOrigin || "");
              frame = document.createElement("iframe");
              frame.setAttribute("title", String(message.title || ""));
              frame.setAttribute("allow", "fullscreen");
              setScale(message.scale);
              frame.addEventListener("error", () => send("frame-error", null));
              frame.src = String(message.sandboxUrl || "about:blank");
              try { setScheme(new URLSearchParams(new URL(frame.src).hash.slice(1)).get("theme")); } catch {}
              document.body.appendChild(frame);
              return;
            }
            if (message.channel === "scale") {
              setScale(message.scale);
              return;
            }
            if (message.channel === "sandbox" && frame && frame.contentWindow) {
              if (message.data && message.data.type === "openclaw:widget-theme") setScheme(message.data.mode);
              frame.contentWindow.postMessage(message.data, sandboxOrigin);
            }
          });
          send("ready", null);
        })();
        </script></body></html>
        """;

    /// <summary>
    /// Parses one relay message (<c>WebMessageAsJson</c>). Callers must have
    /// already verified that the message came from <see cref="PageUrl"/>.
    /// Unknown, malformed, or oversized messages return null.
    /// </summary>
    public static BoardWidgetHostMessage? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxMessageChars)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String)
                return null;
            root.TryGetProperty("data", out var data);
            return channel.GetString() switch
            {
                "ready" => new BoardWidgetHostMessage.PageReady(),
                "frame-error" => new BoardWidgetHostMessage.FrameError(),
                "sandbox" => ParseSandbox(data),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when a navigation targets the relay page itself.</summary>
    public static bool IsPageNavigation(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        string.Equals(parsed.GetLeftPart(UriPartial.Path), PageUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>Native-to-page <c>init</c> message.</summary>
    public static string InitMessage(Uri sandboxUrl, string title, double scale) =>
        JsonSerializer.Serialize(new
        {
            channel = "init",
            sandboxUrl = sandboxUrl.AbsoluteUri,
            sandboxOrigin = BoardWidgetUrlPolicy.OriginString(sandboxUrl),
            title,
            scale = scale > 0 && scale < 1 ? scale : 1,
        });

    /// <summary>Native-to-page preview scale change; the loaded document stays in place.</summary>
    public static string ScaleMessage(double scale) =>
        JsonSerializer.Serialize(new { channel = "scale", scale = scale > 0 && scale < 1 ? scale : 1 });

    /// <summary>Native-to-proxy document delivery (<c>sandbox-resource-ready</c>).</summary>
    public static string ResourceReadyMessage(string html, string renderId) =>
        JsonSerializer.Serialize(new
        {
            channel = "sandbox",
            data = new
            {
                jsonrpc = "2.0",
                method = "ui/notifications/sandbox-resource-ready",
                @params = new { html, renderId },
            },
        });

    /// <summary>
    /// Native-to-proxy widget theme (<c>openclaw:widget-theme</c>). The proxy
    /// adopts the color scheme and forwards tokens to the widget document.
    /// </summary>
    public static string ThemeMessage(bool dark, IReadOnlyDictionary<string, string> tokens) =>
        JsonSerializer.Serialize(new
        {
            channel = "sandbox",
            data = new { type = "openclaw:widget-theme", mode = dark ? "dark" : "light", tokens },
        });

    private static BoardWidgetHostMessage? ParseSandbox(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return null;
        if (data.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            var parameters = data.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            switch (method.GetString())
            {
                case "ui/notifications/sandbox-proxy-ready":
                    return ReadString(parameters, "sandboxUrl") is { } sandboxUrl
                        ? new BoardWidgetHostMessage.ProxyReady(sandboxUrl)
                        : null;
                case "ui/notifications/sandbox-resource-loaded":
                    return ReadString(parameters, "renderId") is { } renderId
                        ? new BoardWidgetHostMessage.ResourceLoaded(renderId)
                        : null;
            }
            return null;
        }
        if (data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
            type.GetString() == "openclaw:widget-size" &&
            data.TryGetProperty("height", out var height) && height.ValueKind == JsonValueKind.Number &&
            height.TryGetDouble(out var value) && double.IsFinite(value) && value > 0)
        {
            return new BoardWidgetHostMessage.ContentSize(Math.Min(value, MaxContentHeight));
        }
        return null;
    }

    private static string? ReadString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>Random script nonce for one page response.</summary>
    public static string NewNonce() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18), Base64FormattingOptions.None)
            .Replace('+', '-').Replace('/', '_');
}
