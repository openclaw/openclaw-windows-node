using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared.Themes;
using OpenClawTray.Dashboards;
using OpenClawTray.Presentation;

namespace OpenClawTray.Theming;

/// <summary>
/// Runs <see cref="ThemeProbe.Script"/> in an offscreen WebView2: a controller parented to a message-only window, never
/// shown, in an in-private profile of the board WebView2 environment. It serves the probe document from memory and
/// answers every other request with 404, so resolving never reaches the network, then closes the controller. WebView2
/// objects live on the UI thread, so the work is marshalled there.
/// <example><code>
/// var json = await new WebViewBuiltinThemeProbe(dispatcher).ResolveAsync(html, ["claw", "tide"], ct);
/// </code></example>
/// </summary>
internal sealed class WebViewBuiltinThemeProbe(IUiDispatcher dispatcher) : IBuiltinThemeProbe
{
    private const string Profile = "openclaw-theme-probe";
    private const string DocumentUri = "https://theme-probe.openclaw.invalid/";
    private static readonly IntPtr HwndMessage = new(-3);

    public Task<string> ResolveAsync(string document, IReadOnlyList<string> themeIds, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RunAsync()
        {
            try
            {
                completion.TrySetResult(await ResolveOnUiThreadAsync(document, themeIds, cancellationToken));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
        if (dispatcher.HasThreadAccess)
            _ = RunAsync();
        else if (!dispatcher.TryEnqueue(() => _ = RunAsync()))
            completion.TrySetException(new InvalidOperationException("The UI thread is not accepting work."));
        return completion.Task;
    }

    private static async Task<string> ResolveOnUiThreadAsync(string document, IReadOnlyList<string> themeIds, CancellationToken cancellationToken)
    {
        var environment = await BoardWebViewEnvironment.GetAsync().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var window = CreateWindowEx(0, "Static", "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the probe window.");
        CoreWebView2Controller? controller = null;
        try
        {
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window),
                BoardWebViewEnvironment.CreateControllerOptions(environment, Profile)).AsTask(cancellationToken);
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            BoardWebViewEnvironment.ApplyLockedDownSettings(core.Settings);
            core.Settings.IsWebMessageEnabled = false;
            BoardWebViewEnvironment.DenyAmbientCapabilities(core);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                args.Response = args.Request.Uri == DocumentUri
                    ? environment.CreateWebResourceResponse(
                        new MemoryStream(Encoding.UTF8.GetBytes(document)).AsRandomAccessStream(), 200, "OK",
                        "Content-Type: text/html; charset=utf-8")
                    : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            };

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, args) => loaded.TrySetResult(args.IsSuccess);
            core.Navigate(DocumentUri);
            if (!await loaded.Task.WaitAsync(cancellationToken))
                throw new InvalidOperationException("The theme probe document did not load.");
            await core.ExecuteScriptAsync(ThemeProbe.Script).AsTask(cancellationToken);
            var encoded = await core.ExecuteScriptAsync(ThemeProbe.ResolveExpression(themeIds)).AsTask(cancellationToken);
            // The script returns a JSON string; ExecuteScriptAsync encodes that value as JSON once more.
            return JsonSerializer.Deserialize<string>(encoded) ?? throw new InvalidOperationException("The theme probe returned nothing.");
        }
        finally
        {
            controller?.Close();
            DestroyWindow(window);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
}
