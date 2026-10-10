using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenClaw.Tray.Tests;

public class ClawHubInstallScriptSmokeTests
{
    [Theory]
    [InlineData(
        "/openclaw/plugins/diagnostics-otel",
        "plugin",
        "diagnostics-otel",
        "clawhub:@openclaw/diagnostics-otel",
        "@openclaw/diagnostics-otel")]
    [InlineData(
        "/alipay/skills/alipay-aipay",
        "skill",
        "@alipay/alipay-aipay",
        "@alipay/alipay-aipay",
        "")]
    [InlineData(
        "/vercel-labs/skills/find-skills",
        "skill",
        "skills-sh:vercel-labs/skills/find-skills",
        "skills-sh:vercel-labs/skills/find-skills",
        "")]
    public async Task Script_LoadsAgainstListingFixtureAndReplacesInstallCommand(
        string listingPath,
        string expectedKind,
        string expectedId,
        string installTarget,
        string expectedPackage)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var fixture = await File.ReadAllTextAsync(Path.Combine(
            root,
            "tests",
            "OpenClaw.Tray.Tests",
            "Fixtures",
            "ClawHubPluginListing.html"));
        var script = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Assets",
            "ClawHub",
            "ClawHubInstallBridge.js"));
        var html = fixture
            .Replace("{{CLAWHUB_INSTALL_BRIDGE}}", script, StringComparison.Ordinal)
            .Replace("{{EXPECTED_KIND}}", expectedKind, StringComparison.Ordinal)
            .Replace("{{EXPECTED_ID}}", expectedId, StringComparison.Ordinal)
            .Replace("{{INSTALL_TARGET}}", installTarget, StringComparison.Ordinal)
            .Replace("{{EXPECTED_PACKAGE}}", expectedPackage, StringComparison.Ordinal);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = ServeOnceAsync(listener, html, timeout.Token);
        var profilePath = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-clawhub-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profilePath);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = FindEdge(),
                Arguments = string.Join(' ',
                    "--headless=new",
                    "--disable-gpu",
                    "--no-first-run",
                    "--no-proxy-server",
                    "--host-resolver-rules=\"MAP clawhub.ai 127.0.0.1\"",
                    $"--user-data-dir=\"{profilePath}\"",
                    "--dump-dom",
                    $"http://clawhub.ai:{port}{listingPath}"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            Assert.NotNull(process);

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            await serverTask;

            Assert.True(process.ExitCode == 0, $"Edge exited with {process.ExitCode}: {error}");
            Assert.True(
                output.Contains(
                    "data-clawhub-script-result=\"pass\"",
                    StringComparison.Ordinal),
                output);
            Assert.Contains("Install via Windows Hub", output, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            try
            {
                Directory.Delete(profilePath, recursive: true);
            }
            catch (IOException)
            {
                // Edge can briefly retain profile handles after process exit.
            }
            catch (UnauthorizedAccessException)
            {
                // Edge can briefly retain profile handles after process exit.
            }
        }
    }

    private static async Task ServeOnceAsync(
        TcpListener listener,
        string html,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
        }

        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft",
                "Edge",
                "Application",
                "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft",
                "Edge",
                "Application",
                "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Edge",
                "Application",
                "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Microsoft Edge is required for the ClawHub script smoke test.");
    }
}
