using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Dashboards;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class DashboardWebsiteIsolationTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TicketFailure_ShowsTheCorrectRecoveryMessage(bool remoteGateway, bool repeatedRejection)
    {
        ReactorHostControl? host = null;
        ChatThemeProofScope? resources = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope("Light");
                var widget = new BoardWidget
                {
                    Name = "ticket-proof",
                    TabId = "main",
                    ContentKind = BoardWidgetContentKind.Html,
                    SizeW = 6,
                    SizeH = 4,
                    Position = 0,
                    Revision = 1,
                    SandboxUrl = "/mcp-app-sandbox",
                    SandboxPort = 18790,
                };
                var props = new BoardWidgetCellProps(widget, BoardWidgetBodyKind.Frame, "HTML",
                    new Uri(remoteGateway ? "https://gateway.example.com/" : "http://127.0.0.1:18789/"),
                    repeatedRejection ? BoardWidgetTicketFailure.RepeatedRejection : BoardWidgetTicketFailure.RefreshUnavailable,
                    _ => { }, _ => { }, _ => { }, (_, _) => { }, ReducedMotion: true);
                host = new ReactorHostControl { Width = 600, Height = 400 };
                host.Mount(_ => Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(props));
                ui.Container.Children.Add(host);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                var expectedKey = !repeatedRejection ? "Dashboards_Widget_ContentUnavailable"
                    : remoteGateway ? "Dashboards_Widget_SandboxOriginRequired"
                    : "Dashboards_Widget_AuthorizationFailed";
                var text = FindDescendants<TextBlock>(host!).Select(block => block.Text).ToArray();
                Assert.Contains(LocalizationHelper.GetString(expectedKey), text);
                if (!repeatedRejection)
                {
                    Assert.DoesNotContain(LocalizationHelper.GetString("Dashboards_Widget_AuthorizationFailed"), text);
                    Assert.DoesNotContain(LocalizationHelper.GetString("Dashboards_Widget_SandboxOriginRequired"), text);
                }
                Assert.Contains(FindDescendants<Button>(host!), button =>
                    button.Content is string label && label == LocalizationHelper.GetString("Dashboards_Retry"));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    ui.Container.Children.Remove(host);
                    host.Dispose();
                }
                resources?.Dispose();
            });
        }
    }

    [Fact]
    public async Task OutOfRangeUpdateTimesRenderAsUnknownInsteadOfThrowing()
    {
        await ui.RunOnUIAsync(() =>
        {
            var unknown = LocalizationHelper.GetString("Dashboards_UpdatedUnknown");
            foreach (var time in new long?[] { null, -1, 0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() + 1, long.MaxValue })
            {
                var row = new DashboardSessionRow { Session = new SessionInfo { Key = "synthetic" }, UpdatedAtMs = time };
                Assert.Equal(unknown, DashboardsPresentation.UpdatedText(row, DateTimeOffset.UtcNow));
            }
            var recent = new DashboardSessionRow
            {
                Session = new SessionInfo { Key = "synthetic" },
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Assert.NotEqual(unknown, DashboardsPresentation.UpdatedText(recent, DateTimeOffset.UtcNow));
        });
    }

    [Fact]
    public async Task SameOriginWebsiteWidgetsDoNotShareCookiesOrLocalStorage()
    {
        await ui.RunOnUIAsync(async () =>
        {
            var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
                ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT for dashboard runtime proof.");
            var resources = (ResourceDictionary)XamlReader.Load(File.ReadAllText(
                Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Themes", "DashboardResources.xaml")));
            Application.Current.Resources.MergedDictionaries.Add(resources);
            try
            {
                var first = new BoardWebsiteHost();
                var second = new BoardWebsiteHost();
                ui.Container.Children.Add(first);
                ui.Container.Children.Add(second);
                var url = new Uri("https://dashboard-proof.example.test/");
                first.Update(url);
                second.Update(url);
                var firstView = await WaitForWebViewAsync(first);
                var secondView = await WaitForWebViewAsync(second);
                var firstCore = firstView.CoreWebView2;
                var secondCore = secondView.CoreWebView2;
                var firstProfile = firstCore.Profile.ProfileName;
                Assert.NotEqual(firstCore.Profile.ProfileName, secondCore.Profile.ProfileName);
                Assert.True(firstCore.Profile.IsInPrivateModeEnabled);
                Assert.True(secondCore.Profile.IsInPrivateModeEnabled);
                await LoadSyntheticWebsiteAsync(firstCore, url);
                await LoadSyntheticWebsiteAsync(secondCore, url);

                var cookie = firstCore.CookieManager.CreateCookie("dashboard-proof", "first-widget", url.Host, "/");
                firstCore.CookieManager.AddOrUpdateCookie(cookie);
                await firstCore.ExecuteScriptAsync("localStorage.setItem('dashboard-proof', 'first-widget')");

                Assert.Contains(await firstCore.CookieManager.GetCookiesAsync(url.AbsoluteUri),
                    item => item.Name == "dashboard-proof" && item.Value == "first-widget");
                Assert.DoesNotContain(await secondCore.CookieManager.GetCookiesAsync(url.AbsoluteUri),
                    item => item.Name == "dashboard-proof");
                Assert.Equal("\"first-widget\"", await firstCore.ExecuteScriptAsync("localStorage.getItem('dashboard-proof')"));
                Assert.Equal("null", await secondCore.ExecuteScriptAsync("localStorage.getItem('dashboard-proof')"));

                var neighborCookie = secondCore.CookieManager.CreateCookie("dashboard-proof", "second-widget", url.Host, "/");
                secondCore.CookieManager.AddOrUpdateCookie(neighborCookie);
                await secondCore.ExecuteScriptAsync(
                    "localStorage.setItem('dashboard-proof', 'second-widget'); window.dashboardNeighbor = 'still-loaded'");

                // Retry replaces only its own WebView/profile and leaves its neighbor usable.
                first.Update(url, attempt: 1);
                var retried = await WaitForWebViewAsync(first);
                Assert.NotSame(firstView, retried);
                Assert.NotEqual(firstProfile, retried.CoreWebView2.Profile.ProfileName);
                Assert.NotEqual(secondCore.Profile.ProfileName, retried.CoreWebView2.Profile.ProfileName);
                await LoadSyntheticWebsiteAsync(retried.CoreWebView2, url);
                Assert.Contains("Synthetic website", await retried.CoreWebView2.ExecuteScriptAsync("document.body.textContent"));
                Assert.DoesNotContain(await retried.CoreWebView2.CookieManager.GetCookiesAsync(url.AbsoluteUri),
                    item => item.Name == "dashboard-proof");
                Assert.Equal("null", await retried.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('dashboard-proof')"));
                Assert.Contains(await secondCore.CookieManager.GetCookiesAsync(url.AbsoluteUri),
                    item => item.Name == "dashboard-proof" && item.Value == "second-widget");
                Assert.Equal("\"second-widget\"", await secondCore.ExecuteScriptAsync("localStorage.getItem('dashboard-proof')"));
                Assert.Equal("\"still-loaded\"", await secondCore.ExecuteScriptAsync("window.dashboardNeighbor"));
            }
            finally
            {
                ui.Container.Children.Clear();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        }).WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static async Task<WebView2> WaitForWebViewAsync(DependencyObject host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var view = FindWebView(host);
            if (view?.CoreWebView2 is not null)
                return view;
            await Task.Delay(25, deadline.Token);
        }
    }

    private static WebView2? FindWebView(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is WebView2 view)
                return view;
            if (FindWebView(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static async Task LoadSyntheticWebsiteAsync(CoreWebView2 core, Uri url)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        core.AddWebResourceRequestedFilter(url.AbsoluteUri + "*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, args) =>
        {
            if (args.Request.Uri.StartsWith(url.AbsoluteUri, StringComparison.Ordinal))
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><title>Website isolation proof</title><p>Synthetic website</p>"))
                        .AsRandomAccessStream(),
                    200, "OK", "Content-Type: text/html; charset=utf-8");
        };
        void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (args.Uri == url.AbsoluteUri + "?proof=1")
                navigationId = args.NavigationId;
        }
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != navigationId)
                return;
            if (args.IsSuccess)
                loaded.TrySetResult();
            else
                loaded.TrySetException(new InvalidOperationException($"Synthetic website navigation failed: {args.WebErrorStatus}"));
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(url.AbsoluteUri + "?proof=1");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Completed;
        }
    }
}
