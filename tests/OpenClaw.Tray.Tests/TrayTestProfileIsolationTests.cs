using System.Diagnostics;
using System.Text.Json;
using OpenClaw.TestSupport;
using OpenClawTray;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

[Collection(OpenClawTrayDataDirEnvironmentCollection.Name)]
public sealed class TrayTestProfileIsolationTests
{
    private const int DefaultProbeTimeoutSeconds = 120;
    private static readonly string[] ProfileVariables =
    [
        TrayTestProfileIsolation.DataVariable,
        TrayTestProfileIsolation.RoamingVariable,
        TrayTestProfileIsolation.LocalVariable,
        TrayTestProfileIsolation.LegacyLocalVariable
    ];

    [Theory]
    [InlineData("default")]
    [InlineData("blank")]
    [InlineData("data-only")]
    [InlineData("roots-only")]
    [InlineData("legacy-only")]
    [InlineData("explicit")]
    public async Task FreshTestHost_IsolatesBeforeStaticChatPathsInitialize(string scenario)
    {
        using var directory = new TempDirectory();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var assemblyPath = typeof(TrayTestProfileIsolationTests).Assembly.Location;
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OpenClaw.Tray.ProfileProbe.dll"));
        start.ArgumentList.Add(typeof(TrayTestProfileIsolationTests).Assembly.Location);
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        foreach (var variable in ProfileVariables)
            start.Environment[variable] = scenario == "blank" ? " " : "";

        if (scenario is "data-only" or "explicit")
            start.Environment[TrayTestProfileIsolation.DataVariable] = directory.Combine("Direct");
        if (scenario is "roots-only" or "explicit")
        {
            start.Environment[TrayTestProfileIsolation.RoamingVariable] = directory.Combine("Roaming");
            start.Environment[TrayTestProfileIsolation.LocalVariable] = directory.Combine("Local");
        }
        if (scenario is "legacy-only" or "explicit")
            start.Environment[TrayTestProfileIsolation.LegacyLocalVariable] = directory.Combine("LegacyDirect");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the isolated profile probe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var timeoutSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_TEST_PROFILE_PROBE_TIMEOUT_SECONDS"),
            out var configuredTimeout) && configuredTimeout > 0
                ? configuredTimeout
                : DefaultProbeTimeoutSeconds;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Isolated profile probe exceeded {timeoutSeconds} seconds.");
        }

        var diagnostics = $"{await output}\n{await error}";
        Assert.True(process.ExitCode == 0, diagnostics);
        using var report = JsonDocument.Parse(await output);
        var snapshot = report.RootElement;
        string? Read(string name) => snapshot.GetProperty(name).GetString();
        var data = Read("Data");
        var roaming = Read("Roaming");
        var local = Read("Local");
        var legacy = Read("Legacy");
        Assert.False(string.IsNullOrWhiteSpace(data));
        Assert.False(string.IsNullOrWhiteSpace(roaming));
        Assert.False(string.IsNullOrWhiteSpace(legacy));
        Assert.True(!string.IsNullOrWhiteSpace(local) || scenario == "legacy-only");

        if (scenario is "data-only" or "explicit")
            Assert.Equal(directory.Combine("Direct"), data);
        else
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests") +
                Path.DirectorySeparatorChar, data);
        if (scenario is "roots-only" or "explicit")
        {
            Assert.Equal(directory.Combine("Roaming"), roaming);
            Assert.Equal(directory.Combine("Local"), local);
        }
        if (scenario is "legacy-only" or "explicit")
            Assert.Equal(directory.Combine("LegacyDirect"), legacy);
        if (scenario == "legacy-only")
            Assert.Null(local);

        Assert.Equal(data, Read("LocalData"));
        Assert.Equal(data, Read("RoamingData"));
        Assert.True(snapshot.GetProperty("IsIsolated").GetBoolean());
        Assert.Equal(Path.Combine(data!, "settings.json"), Read("SettingsPath"));
        Assert.Equal(Path.Combine(data!, "last-chat-state.json"), Read("LastChatState"));
        Assert.Equal(Path.Combine(data!, "aborted-messages.json"), Read("AbortedIds"));
        Assert.Equal(Path.Combine(data!, "tool-metadata.json"), Read("ToolMetadata"));
        Assert.Equal(
            local is null ? legacy : Path.Combine(local, AppIdentity.DataDirectoryName),
            Read("SetupLocal"));
        foreach (var path in new[] { data, roaming, local, legacy }.Where(path => path is not null))
            Assert.True(Path.IsPathFullyQualified(path!));
    }

    [Fact]
    public void Logger_UsesIsolatedLocalDataDirectory()
    {
        // Logger's static constructor performs I/O, so exercise it only in the fully isolated parent.
        var path = Logger.LogFilePath;
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData
                 })
        {
            foreach (var name in new[] { "OpenClawTray", "OpenClawTray-Dev" })
                Assert.False(path.StartsWith(Path.Combine(Environment.GetFolderPath(folder), name),
                    StringComparison.OrdinalIgnoreCase));
        }
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "Logger.cs"));
        Assert.Contains("_logDirectory = AppIdentity.ResolveLocalDataDirectory();", source);
        Assert.Contains("_logFilePath = Path.Combine(_logDirectory, \"openclaw-tray.log\");", source);
    }

    [Theory]
    [InlineData("OPENCLAW_TRAY_DATA_DIR", "OpenClawTray")]
    [InlineData("OPENCLAW_TRAY_DATA_DIR", "OpenClawTray-Dev")]
    [InlineData("OPENCLAW_TRAY_DATA_DIR", "OpenClawTray\\child")]
    [InlineData("OPENCLAW_TRAY_APPDATA_DIR", "")]
    [InlineData("OPENCLAW_TRAY_APPDATA_DIR", "OpenClawTray")]
    [InlineData("OPENCLAW_TRAY_APPDATA_DIR", "OpenClawTray-Dev")]
    [InlineData("OPENCLAW_TRAY_LOCALAPPDATA_DIR", "")]
    [InlineData("OPENCLAW_TRAY_LOCALAPPDATA_DIR", "OpenClawTray")]
    [InlineData("OPENCLAW_TRAY_LOCALAPPDATA_DIR", "OpenClawTray-Dev")]
    [InlineData("OPENCLAW_TRAY_LOCAL_DATA_DIR", "OpenClawTray")]
    [InlineData("OPENCLAW_TRAY_LOCAL_DATA_DIR", "OpenClawTray-Dev")]
    public void ExplicitRealProfile_IsRejectedWithoutAccessingContents(string variable, string suffix)
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData
                 })
        {
            var realRoot = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(realRoot))
                continue;
            using var environment = new EnvironmentScope(variable,
                Path.Combine(realRoot, suffix.Replace('\\', Path.DirectorySeparatorChar)));
            var error = Assert.Throws<InvalidOperationException>(TrayTestProfileIsolation.Initialize);
            Assert.Contains(variable, error.Message);
        }
    }

    [Theory]
    [InlineData("OPENCLAW_TRAY_DATA_DIR")]
    [InlineData("OPENCLAW_TRAY_APPDATA_DIR")]
    [InlineData("OPENCLAW_TRAY_LOCALAPPDATA_DIR")]
    [InlineData("OPENCLAW_TRAY_LOCAL_DATA_DIR")]
    public void RelativeOverrides_AreRejectedBeforeAnyEnvironmentChange(string variable)
    {
        using var environment = new EnvironmentScope(variable, "relative-profile");
        var originals = ProfileVariables.Select(Environment.GetEnvironmentVariable).ToArray();
        Assert.Throws<InvalidOperationException>(TrayTestProfileIsolation.Initialize);
        Assert.Equal(originals, ProfileVariables.Select(Environment.GetEnvironmentVariable).ToArray());
    }

    [Fact]
    public void TemporaryEnvironmentScope_RestoresStartupIsolation()
    {
        var originals = ProfileVariables.Select(Environment.GetEnvironmentVariable).ToArray();
        using (var directory = new TempDirectory())
        using (var environment = new EnvironmentScope())
        {
            foreach (var variable in ProfileVariables)
                environment.Set(variable, directory.Combine(variable));
            TrayTestProfileIsolation.Initialize();
            foreach (var variable in ProfileVariables)
                Assert.Equal(directory.Combine(variable), Environment.GetEnvironmentVariable(variable));
        }
        Assert.Equal(originals, ProfileVariables.Select(Environment.GetEnvironmentVariable).ToArray());
    }
}
