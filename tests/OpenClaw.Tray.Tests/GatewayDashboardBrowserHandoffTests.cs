using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardBrowserHandoffTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
    private static GatewayRecord Local => new()
    {
        Id = "owned", Url = "ws://localhost:18789/control/", IsLocal = true,
        SetupManagedDistroName = "OpenClawGateway"
    };
    private static string Payload(string ticket = "synthetic-ticket") => JsonSerializer.Serialize(new
    {
        ok = true,
        browserUrl = "http://127.0.0.1:18789/control/#bootstrapToken=" + ticket +
            "&bootstrapProfile=owner&gatewayUrl=ws%3A%2F%2F127.0.0.1%3A18789%2Fcontrol%2F",
        browserBootstrapExpiresAtMs = Now.AddMinutes(10).ToUnixTimeMilliseconds(),
        httpUrl = "http://127.0.0.1:18789/control/", wsUrl = "ws://127.0.0.1:18789/control/",
        port = 18789, tokenIncluded = false,
        url = "must-not-use-legacy-url", token = "must-not-use-legacy-token"
    });

    [Theory]
    [InlineData(null, "/control")]
    [InlineData("settings/updates", "/control/settings/updates")]
    public async Task DeviceOnlyOwner_OpensCanonicalHandoffWithoutExportingNativeCredential(string? path, string expectedPath)
    {
        var runner = new Runner();
        string? opened = null;
        var proofs = 0;
        var launcher = Create(runner, url => { opened = url; return Task.FromResult(true); },
            inspect: _ => { proofs++; return true; });

        Assert.True(await launcher.OpenAsync(path));
        var url = new Uri(opened!);
        Assert.Equal(expectedPath, url.AbsolutePath);
        Assert.Equal(new Uri(JsonNode.Parse(Payload())!["browserUrl"]!.GetValue<string>()).Fragment, url.Fragment);
        Assert.DoesNotContain("native-device-token", opened);
        Assert.DoesNotContain("legacy", opened);
        Assert.Equal(2, proofs);
        Assert.Equal("OpenClawGateway", runner.Distro);
        Assert.Equal(new[] { "bash", "-s" }, runner.Command);
        Assert.Contains("/proc/$pid/environ", runner.Script);
        Assert.Contains("/proc/$pid/cwd", runner.Script);
        Assert.Contains("compgen -e", runner.Script);
        Assert.Contains("export \"$entry\"", runner.Script);
        Assert.DoesNotContain("exec env -i", runner.Script);
        Assert.Contains("dashboard --json", runner.Script);
        Assert.DoesNotContain("update.run", runner.Script);
        Assert.DoesNotContain("gateway restart", runner.Script);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("ssh")]
    [InlineData("native")]
    [InlineData("manual-local")]
    [InlineData("shared")]
    public async Task OtherOwnersAndSharedCredentials_KeepExistingBrowserRoute(string kind)
    {
        var target = kind switch
        {
            "remote" => Local with { Url = "wss://remote.example/control", IsLocal = false, SetupManagedDistroName = null },
            "ssh" => Local with { SshTunnel = new("user", "ssh.example", 18789, 18789) },
            "native" => Local with { NativePackageFamilyName = "native-package" },
            "manual-local" => Local with { SetupManagedDistroName = null },
            _ => Local
        };
        var runner = new Runner();
        string? opened = null;
        var launcher = Create(runner, url => { opened = url; return Task.FromResult(true); },
            getActive: () => target, inspect: _ => throw new InvalidOperationException("Not a WSL handoff"),
            shared: kind == "shared");
        Assert.True(await launcher.OpenAsync("settings/updates"));
        Assert.Equal(0, runner.Calls);
        Assert.Equal(GatewayDashboardUrlBuilder.Build(target.Url, "settings/updates",
            "native-device-token", kind == "shared"), opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOwnershipBeforeOrAfterIssuance_NeverDeliversTicket(bool afterIssuance)
    {
        var runner = new Runner();
        var checks = 0;
        var failures = 0;
        var launcher = Create(runner, _ => throw new InvalidOperationException("Must not launch"),
            inspect: _ => afterIssuance && ++checks == 1, failure: () => failures++);
        Assert.False(await launcher.OpenAsync("settings/updates"));
        Assert.Equal(afterIssuance ? 1 : 0, runner.Calls);
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrSelectionChangeWhileIssuing_NeverDeliversTicket(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var active = Local;
        var runner = new Runner { Invoke = () =>
        {
            if (cancel) cancellation.Cancel(); else active = Local with { Id = "other" };
            return new(0, Payload(), "");
        } };
        var failures = 0;
        var launcher = Create(runner, _ => throw new InvalidOperationException("Must not launch"),
            getActive: () => active, failure: () => failures++);
        Assert.False(await launcher.OpenAsync("settings/updates", cancellation.Token));
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("long-lived")]
    [InlineData("wrong-port")]
    [InlineData("wrong-base")]
    [InlineData("wrong-ws")]
    [InlineData("wrong-browser")]
    [InlineData("wrong-fragment")]
    [InlineData("cli-error")]
    [InlineData("missing-ok")]
    [InlineData("malformed")]
    public void InvalidPayloads_RejectWithoutReturningOrWrappingSecretInput(string kind)
    {
        var root = JsonNode.Parse(Payload())!.AsObject();
        switch (kind)
        {
            case "expired": root["browserBootstrapExpiresAtMs"] = Now.ToUnixTimeMilliseconds(); break;
            case "long-lived": root["browserBootstrapExpiresAtMs"] = Now.AddMinutes(11).ToUnixTimeMilliseconds(); break;
            case "wrong-port": root["port"] = 18790; break;
            case "wrong-base": root["httpUrl"] = "http://localhost:18789/other/"; break;
            case "wrong-ws": root["wsUrl"] = "ws://other.example:18789/control/"; break;
            case "wrong-browser": root["browserUrl"] = "https://other.example/#bootstrapToken=synthetic-ticket"; break;
            case "wrong-fragment": root["browserUrl"] = root["browserUrl"]!.GetValue<string>().Replace("Profile=owner", "Profile=other"); break;
            case "cli-error": root["ok"] = false; root["error"] = "synthetic-ticket"; break;
            case "missing-ok": root.Remove("ok"); break;
        }
        var input = kind == "malformed" ? "invalid synthetic-ticket" : root.ToJsonString();
        var error = Assert.Throws<InvalidOperationException>(() =>
            GatewayDashboardLauncher.BuildBrowserHandoffUrl(input, Local, "settings/updates", Now));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-ticket", error.ToString());
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("indeterminate")]
    [InlineData("legacy")]
    public async Task UnavailableIssuer_PreservesCleanRouteForAuthorizedBrowser(string kind)
    {
        var runner = new Runner { Invoke = () => kind == "legacy"
            ? new(0, "{\"url\":\"synthetic-secret-url\",\"token\":\"synthetic-secret-token\"}", "")
            : new(kind == "failure" ? 1 : 0, "synthetic-secret-output", "synthetic-secret-error",
                OutcomeIndeterminate: kind == "indeterminate") };
        var failures = 0;
        string? opened = null;
        var launcher = Create(runner, url => { opened = url; return Task.FromResult(true); }, failure: () => failures++);
        Assert.True(await launcher.OpenAsync("settings/updates"));
        Assert.Equal("http://localhost:18789/control/settings/updates", opened);
        Assert.Equal(0, failures);
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StoppedGateway_UsesCleanRouteButStillRejectsForeignResponse(bool stopsDuringIssuance, bool foreignResponse)
    {
        var runner = new Runner();
        if (foreignResponse)
            runner.Invoke = () => new(0, Payload().Replace("ws://127.0.0.1:18789/control/", "ws://foreign.example/control/"), "");
        var checks = 0;
        string? opened = null;
        var launcher = Create(runner, url => { opened = url; return Task.FromResult(true); },
            inspectKind: _ => stopsDuringIssuance && ++checks == 1
                ? GatewayEndpointProvenanceKind.ExpectedManagedGateway : GatewayEndpointProvenanceKind.NoListener);
        Assert.Equal(!foreignResponse, await launcher.OpenAsync("settings/updates"));
        if (foreignResponse) Assert.Null(opened);
        else
        {
            Assert.Equal("http://localhost:18789/control/settings/updates", opened);
            Assert.DoesNotContain("Token", opened);
        }
        Assert.Equal(stopsDuringIssuance ? 1 : 0, runner.Calls);
    }

    [Fact]
    public async Task ExplicitBrowserRetry_IssuesNewSingleUseTicketInsteadOfReusingPreviousUrl()
    {
        var runner = new Runner();
        var urls = new List<string>();
        runner.Invoke = () => new(0, Payload("synthetic-ticket-" + runner.Calls), "");
        var launcher = Create(runner, url => { urls.Add(url); return Task.FromResult(urls.Count == 2); });
        Assert.False(await launcher.OpenAsync("settings/updates"));
        Assert.True(await launcher.OpenAsync("settings/updates"));
        Assert.NotEqual(urls[0], urls[1]);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task TicketExpiresDuringFinalOwnerCheck_IsNotDelivered()
    {
        var clock = new Clock();
        var checks = 0;
        var launcher = Create(new Runner(), _ => throw new InvalidOperationException("Must not launch"),
            inspect: _ => { if (++checks == 2) clock.Current = Now.AddMinutes(11); return true; }, clock: clock);
        Assert.False(await launcher.OpenAsync("settings/updates"));
        Assert.Equal(2, checks);
    }

    private static GatewayDashboardLauncher Create(Runner runner, Func<string, Task<bool>> open,
        Func<GatewayRecord?>? getActive = null, Func<GatewayRecord, bool>? inspect = null,
        Action? failure = null, bool shared = false, TimeProvider? clock = null,
        Func<GatewayRecord, GatewayEndpointProvenanceKind>? inspectKind = null) => new(
        () => true,
        () => new((getActive?.Invoke() ?? Local).Url, "native-device-token", false,
            shared ? CredentialResolver.SourceSharedGatewayToken : CredentialResolver.SourceDeviceToken),
        open, failure ?? (() => { }), getActiveGateway: getActive ?? (() => Local),
        inspectWslOwner: (record, _) => Task.FromResult(new GatewayEndpointProvenance(
            inspectKind?.Invoke(record) ?? ((inspect?.Invoke(record) ?? true)
                ? GatewayEndpointProvenanceKind.ExpectedManagedGateway : GatewayEndpointProvenanceKind.UnknownListener), 18789)),
        wslCommands: runner, timeProvider: clock ?? new Clock());

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Current = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    private sealed class Runner : IWslCommandRunner
    {
        public int Calls;
        public string? Distro;
        public IReadOnlyList<string>? Command;
        public string? Script;
        public Func<WslCommandResult>? Invoke;
        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command,
            CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null)
        {
            Calls++;
            Distro = name; Command = command; Script = standardInput;
            return Task.FromResult(Invoke?.Invoke() ?? new WslCommandResult(0, Payload(), ""));
        }
        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
