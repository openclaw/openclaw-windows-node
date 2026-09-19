using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Connection;
using OpenClaw.Shared.Browser;

namespace OpenClaw.E2ETests.Setup;

public sealed class BrowserWslTraceFactAttribute : FactAttribute
{
    public BrowserWslTraceFactAttribute()
    {
        if (!E2ETestGate.IsEnabled || Environment.GetEnvironmentVariable("OPENCLAW_BROWSER_WSL_TRACE") != "1")
            Skip = "Dedicated disposable hosted WSL component trace only.";
    }
}

/// <summary>Test-only stand-in after unchanged production admission. Not credential or whole-Companion proof.</summary>
public sealed class BrowserWslGuestTraceTests
{
    private static readonly string[] Cases = ["normal", "ipc_eof", "client_cancel", "deadline", "forced_client_exit", "pipe_retirement"];
    private const string Cli = "/home/openclaw/.openclaw/bin/openclaw";
    private readonly List<object> _cases = [];
    private readonly List<string> _errors = [];
    private string _root = "";
    private string _stage = "initialization";
    private long _gatewayPid;

    [BrowserWslTraceFact]
    public async Task ExactOwner_ObservesGuestSettlementWithoutTreatingWindowsJoinAsAcknowledgment()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        var receiptPath = Environment.GetEnvironmentVariable("OPENCLAW_WSL_TRACE_RECEIPT");
        Assert.False(string.IsNullOrWhiteSpace(receiptPath));
        var fixture = new E2ESetupFixture();
        Assert.StartsWith("OpenClawE2E-", fixture.DistroName);
        _root = "/home/openclaw/.openclaw/browser-wsl-trace-" + Guid.NewGuid().ToString("N");
        var output = Console.Out;
        var error = Console.Error;
        var installed = false;
        var restored = false;
        var removed = false;
        // Existing setup fixture logs include ephemeral credentials/config. Do not forward
        // them to test/Actions output, and never upload TestResults/E2E for this workflow.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            _stage = "managed_fixture_setup";
            await fixture.InitializeAsync();
            Assert.Null(fixture.SetupError);
            await fixture.StopTrayAsync(); // Exact fixture-owned tray, never an unrelated app/gateway.
            _stage = "provenance_preflight";
            var preflight = await Guest(fixture, "test \"$(id -un)\" = openclaw; test \"$HOME\" = /home/openclaw; test -x /usr/bin/python3; systemctl --user show openclaw-gateway.service -p MainPID --value");
            _gatewayPid = long.Parse(preflight.Trim());
            Assert.True(_gatewayPid > 1);
            var repository = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
            var helper = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(repository, "scripts", "BrowserWslGuestTrace.py")));
            _stage = "install_controlled_guest_cli";
            // Set before mutation, so partial setup also enters exact-path restoration.
            installed = true;
            await Guest(fixture, $"""
                set -euo pipefail
                umask 077
                mkdir '{_root}'
                mkdir -p /home/openclaw/.openclaw/bin
                if test -e '{Cli}' || test -L '{Cli}'; then mv '{Cli}' '{_root}/original-cli'; fi
                printf '%s' '{helper}' | base64 -d > '{_root}/guest.py'
                printf '%s\n' '#!/bin/sh' 'exec /usr/bin/python3 {_root}/guest.py owner "$@"' > '{Cli}'
                chmod 700 '{Cli}'
                """);
            foreach (var name in Cases)
            {
                _stage = name;
                await TraceCase(fixture, name);
            }
        }
        catch (Exception ex)
        {
            _errors.Add(_stage + ":" + ex.GetType().Name); // Never ex.Message or raw command output.
        }
        finally
        {
            if (installed)
            {
                try
                {
                    await Guest(fixture, $"""
                        set -euo pipefail
                        if test -f '{_root}/case'; then /usr/bin/python3 '{_root}/guest.py' cleanup >/dev/null; fi
                        if test -f '{Cli}' && grep -Fq '{_root}/guest.py' '{Cli}'; then rm '{Cli}'; fi
                        if test -e '{_root}/original-cli' || test -L '{_root}/original-cli'; then
                          test ! -e '{Cli}' && test ! -L '{Cli}'
                          mv '{_root}/original-cli' '{Cli}'
                        fi
                        test "$(systemctl --user show openclaw-gateway.service -p MainPID --value)" = '{_gatewayPid}'
                        rm -rf '{_root}'
                        """);
                    restored = true;
                }
                catch (Exception ex) { _errors.Add("restore:" + ex.GetType().Name); }
            }
            try
            {
                await fixture.DisposeAsync();
                // The existing fixture owns and uninstalls only its unique distro.
                var list = await WindowsWslList();
                removed = !list.Contains(fixture.DistroName, StringComparison.Ordinal);
                if (!removed) _errors.Add("owned_distro_still_registered");
            }
            catch (Exception ex) { _errors.Add("fixture_disposal:" + ex.GetType().Name); }
            Console.SetOut(output);
            Console.SetError(error);
            var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            var receipt = new
            {
                schema = 2,
                sourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                productionPin = "783f178ca5579d057d4799fceb5cec5e8c4d69f8",
                scope = "unchanged WSL command and current-user pipe; controlled guest CLI, not real credentials or full Companion",
                customProofPoolRan = false,
                owner = new
                {
                    executable = "%SystemRoot%/System32/wsl.exe",
                    imageSha256 = File.Exists(wsl) ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(wsl))).ToLowerInvariant() : null,
                    argv = new[] { "--distribution", fixture.DistroName, "--exec", "/bin/bash", "--noprofile", "--norc", "-s" },
                    scriptSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BrowserBootstrapWslCommand.Script))).ToLowerInvariant(),
                    productionDeadlineSeconds = 15,
                    pipeDeadlineSeconds = 20,
                    gatewayMainPid = _gatewayPid,
                    guestProvenanceReportedAsBooleansOnly = true
                },
                cases = _cases,
                errors = _errors,
                setupEvidence = SetupEvidence(fixture.ArtifactDir),
                cleanup = new { originalCliRestored = restored, ownedDistroRemoved = removed },
                status = _errors.Count == 0 && _cases.Count == Cases.Length && restored && removed ? "trace_complete_not_a_settlement_pass" : "trace_incomplete"
            };
            await File.WriteAllTextAsync(receiptPath!, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.Empty(_errors);
        Assert.Equal(Cases.Length, _cases.Count);
        Assert.True(restored && removed);
    }

    private async Task TraceCase(E2ESetupFixture fixture, string name)
    {
        await Guest(fixture, $"mkdir '{_root}/{name}'; printf '%s' '{name}' > '{_root}/case'");
        var clock = Stopwatch.StartNew();
        long commandEndTicks = 0, handlerEndTicks = 0, clientEndTicks = 0, pipeDisposedTicks = 0;
        var commandSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? commandOutcome = null, clientOutcome = null;
        var pipeName = "OpenClaw.WslTrace." + Guid.NewGuid().ToString("N");
        using var ownership = new BrowserWslNativeOwnership(clock);
        var server = new BrowserBootstrapPipeServer(async (request, ct) =>
        {
            try
            {
                _ = BrowserNativeProtocol.ParseRequest(request);
                var command = BrowserBootstrapWslCommand.RunAsync(fixture.DistroName, ct);
                _ = command.ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref commandEndTicks, clock.ElapsedTicks);
                    commandSignal.TrySetResult();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try { _ = await command; commandOutcome = "returned"; }
                catch (Exception ex) { commandOutcome = ex.GetType().Name; throw; }
                return BrowserNativeProtocol.Failure("pairing_unavailable");
            }
            finally { Interlocked.Exchange(ref handlerEndTicks, clock.ElapsedTicks); }
        }, pipeName);
        server.Start();
        using var cancellation = new CancellationTokenSource();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        BrowserWslPersistentObserver? observer = null;
        Task? client = null, pipeRetirement = null, management = null;
        bool cleanupSettled = false;
        try
        {
            await pipe.ConnectAsync(budget.Token);
            client = ownership.Run(ObserveClient);
            JsonElement initial;
            var readyDeadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                initial = await Observe(fixture); // Startup only. Never used for ordering or post-boundary proof.
                if (Ready(initial)) break;
                Assert.False(commandSignal.Task.IsCompleted, "Owner ended before fixture readiness.");
                Assert.True(DateTime.UtcNow < readyDeadline, "Fixture did not become ready.");
                await Task.Delay(75, budget.Token);
            } while (true);
            using var windowsOwner = await FindOwner(fixture.DistroName);
            var windowsStart = windowsOwner.StartTime.ToUniversalTime().Ticks;
            observer = new BrowserWslPersistentObserver(fixture.DistroName, _root, clock);
            var armed = await observer.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            var identities = armed.Data.GetProperty("identities").EnumerateArray().ToArray();
            Assert.Equal(3, identities.Length);
            foreach (var process in identities)
            {
                var original = initial.GetProperty("records").EnumerateArray().Single(p => p.GetProperty("role").GetString() == process.GetProperty("role").GetString());
                Assert.Equal(original.GetProperty("pid").GetInt32(), process.GetProperty("pid").GetInt32());
                Assert.Equal(original.GetProperty("start").GetInt64(), process.GetProperty("start").GetInt64());
                Assert.NotEqual(process.GetProperty("group").GetInt32(), armed.Data.GetProperty("observer").GetProperty("group").GetInt32());
            }
            Assert.False(commandSignal.Task.IsCompleted, "Observer was not armed before owner completion.");
            // Real serialization starts while activation is held. No platform callback waits for observation.
            var managementRequestedTicks = clock.ElapsedTicks;
            management = ownership.Retire();
            var actionTicks = clock.ElapsedTicks;
            Assert.True(armed.ReceivedTicks < actionTicks);
            switch (name)
            {
                case "normal": await Guest(fixture, $"touch '{_root}/{name}/release'"); break;
                case "ipc_eof": pipe.Dispose(); break;
                case "client_cancel": cancellation.Cancel(); break;
                case "forced_client_exit":
                    Assert.Equal(windowsStart, windowsOwner.StartTime.ToUniversalTime().Ticks);
                    windowsOwner.Kill();
                    break;
                case "pipe_retirement":
                    pipeRetirement = server.DisposeAsync().AsTask();
                    _ = pipeRetirement.ContinueWith(_ => Interlocked.Exchange(ref pipeDisposedTicks, clock.ElapsedTicks),
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    break;
                case "deadline": break;
                default: throw new InvalidOperationException("Unknown trace case.");
            }
            var observationEnd = Stopwatch.GetTimestamp() + 25 * Stopwatch.Frequency;
            await Task.WhenAny(commandSignal.Task, Task.Delay(TimeSpan.FromSeconds(25)));
            var first = await observer.Challenge(1);
            var remaining = Math.Max(0, (observationEnd - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            var owners = Task.WhenAll(client, management, pipeRetirement ?? Task.CompletedTask);
            await Task.WhenAny(owners, Task.Delay(TimeSpan.FromSeconds(remaining)));
            var second = await observer.Challenge(2);
            var events = observer.Events;
            foreach (var item in events.Where(e => e.Data.GetProperty("type").GetString() == "exit"))
            {
                var bound = identities.Single(p => p.GetProperty("role").GetString() == item.Data.GetProperty("role").GetString());
                Assert.Equal(bound.GetProperty("pid").GetInt32(), item.Data.GetProperty("pid").GetInt32());
                Assert.Equal(bound.GetProperty("start").GetInt64(), item.Data.GetProperty("start").GetInt64());
            }
            var exits = events.Where(e => e.Data.GetProperty("type").GetString() == "exit").ToArray();
            Assert.Equal(exits.Length, exits.Select(e => e.Data.GetProperty("role").GetString()).Distinct().Count());
            bool Before(long boundary) => boundary > 0 && exits.Length == 3 && exits.All(e => e.ReceivedTicks < boundary);
            bool RunningChallenge(BrowserWslPersistentObserver.Event item)
            {
                var records = item.Data.GetProperty("records").EnumerateArray().ToArray();
                Assert.Equal(3, records.Length);
                foreach (var record in records)
                {
                    var bound = identities.Single(p => p.GetProperty("role").GetString() == record.GetProperty("role").GetString());
                    Assert.Equal(bound.GetProperty("pid").GetInt32(), record.GetProperty("pid").GetInt32());
                    Assert.Equal(bound.GetProperty("start").GetInt64(), record.GetProperty("start").GetInt64());
                }
                return records.Any(p => p.GetProperty("identityPresent").GetBoolean() && p.GetProperty("running").GetBoolean());
            }
            var commandBoundary = Interlocked.Read(ref commandEndTicks);
            var releaseBoundary = Interlocked.Read(ref ownership.ActivationReleaseBeginTicks);
            var mutationBoundary = Interlocked.Read(ref ownership.ManagementMutationTicks);
            var managementBoundary = Interlocked.Read(ref ownership.ManagementCompletionTicks);
            var commandSurvivor = commandBoundary > 0 && first.SentTicks > commandBoundary && RunningChallenge(first.Response);
            var retirementSurvivor = managementBoundary > 0 && second.SentTicks > managementBoundary && RunningChallenge(second.Response);
            _cases.Add(new
            {
                name, clockFrequency = Stopwatch.Frequency,
                windowsIdentity = new { pid = windowsOwner.Id, startUtcTicks = windowsStart, exactSelectedDistroArgv = true },
                observerReady = armed, actionTicks, managementRequestedTicks,
                commandCompletionHookTicks = commandBoundary, handlerEndTicks = Interlocked.Read(ref handlerEndTicks),
                pipeClientCompletionHookTicks = Interlocked.Read(ref clientEndTicks), pipeDisposalHookTicks = Interlocked.Read(ref pipeDisposedTicks),
                runtimeLeaseReleaseTicks = Interlocked.Read(ref ownership.RuntimeLeaseReleaseTicks),
                activationReleaseBeginTicks = releaseBoundary, activationReleasedTicks = Interlocked.Read(ref ownership.ActivationReleasedTicks),
                managementMutationTicks = mutationBoundary, managementCompletionTicks = managementBoundary,
                ownership.ManagementCode, commandOutcome, clientOutcome,
                nativeBoundaryScope = "source-linked unchanged NativeRegistrationRuntime/RegistrationService; synthetic platform facts and real mutex; no shipping helper/registry/Chrome-frame claim",
                completionHookMethod = "ExecuteSynchronously requested, registered while command alive; activation release stamped synchronously before ReleaseMutex; no observer wait in owner callbacks",
                exitEvents = exits,
                firstChallenge = new { first.SentTicks, first.Response }, secondChallenge = new { second.SentTicks, second.Response },
                allExitNotificationsBeforeCommandHook = Before(commandBoundary),
                allExitNotificationsBeforeActivationRelease = Before(releaseBoundary),
                allExitNotificationsBeforeManagementMutation = Before(mutationBoundary),
                causalPostCommandSurvivor = commandSurvivor, causalPostManagementSurvivor = retirementSurvivor,
                provenance = initial.GetProperty("provenance"),
                settlementVerdict = commandSurvivor || retirementSurvivor ? "RED_causal_post_boundary_survivor" :
                    Before(commandBoundary) && Before(releaseBoundary) && Before(mutationBoundary) ? "OBSERVED_exit_notification_precedence" : "UNKNOWN_boundary_ordering"
            });
        }
        finally
        {
            // Neither observer shutdown nor request cleanup can improve a captured pre-cleanup verdict.
            Exception? observerFailure = null;
            try { if (observer is not null) await observer.DisposeAsync(); }
            catch (Exception ex) { observerFailure = ex; }
            await Guest(fixture, $"/usr/bin/python3 '{_root}/guest.py' cleanup >/dev/null");
            cancellation.Cancel();
            pipe.Dispose();
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(8);
            do
            {
                if (!Running(await Observe(fixture))) { cleanupSettled = true; break; }
                await Task.Delay(100);
            } while (DateTime.UtcNow < cleanupDeadline);
            if (client is not null) await client.WaitAsync(TimeSpan.FromSeconds(10));
            if (management is not null) await management.WaitAsync(TimeSpan.FromSeconds(10));
            if (pipeRetirement is not null) await pipeRetirement.WaitAsync(TimeSpan.FromSeconds(10));
            else await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(cleanupSettled, "Owned guest cleanup did not settle.");
            Assert.True(handlerEndTicks > 0, "Handler did not complete after cleanup.");
            if (observerFailure is not null) throw new IOException("Observer cleanup failed.", observerFailure);
        }

        async Task ObserveClient()
        {
            try
            {
                var exchange = BrowserBootstrapPipeClient.ExchangeAsync(pipe,
                    Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"bootstrap\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAA\"}"), cancellation.Token);
                _ = exchange.ContinueWith(_ => Interlocked.Exchange(ref clientEndTicks, clock.ElapsedTicks),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _ = await exchange;
                clientOutcome = "response_and_server_eof";
            }
            catch (Exception ex) { clientOutcome = ex.GetType().Name; }
        }
    }

    private static async Task<Process> FindOwner(string distro)
    {
        Assert.Matches("^OpenClawE2E-[a-f0-9]{8}$", distro);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"Get-CimInstance Win32_Process -Filter \"ParentProcessId={Environment.ProcessId} AND Name='wsl.exe'\" | Where-Object {{ $_.CommandLine.Contains('--distribution {distro} --exec /bin/bash --noprofile --norc -s') }} | ForEach-Object {{ $_.ProcessId }}" })
            start.ArgumentList.Add(arg);
        using var query = Process.Start(start)!;
        var output = query.StandardOutput.ReadToEndAsync();
        var error = query.StandardError.ReadToEndAsync();
        await WaitForInspection(query, TimeSpan.FromSeconds(5));
        await error;
        Assert.Equal(0, query.ExitCode);
        var matches = (await output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Assert.Single(matches);
        return Process.GetProcessById(matches[0]);
    }

    private async Task<JsonElement> Observe(E2ESetupFixture fixture)
    {
        using var document = JsonDocument.Parse(await Guest(fixture, $"/usr/bin/python3 '{_root}/guest.py' observe"));
        return document.RootElement.Clone();
    }

    private static bool Ready(JsonElement observation) =>
        observation.GetProperty("records").GetArrayLength() == 3 &&
        observation.GetProperty("provenance").ValueKind == JsonValueKind.Object &&
        observation.GetProperty("provenance").EnumerateObject().All(p => p.Value.GetBoolean()) &&
        observation.GetProperty("records").EnumerateArray().All(p => p.GetProperty("running").GetBoolean());

    private static bool Running(JsonElement observation) => observation.GetProperty("records").EnumerateArray().Any(p => p.GetProperty("running").GetBoolean());

    private static async Task<string> Guest(E2ESetupFixture fixture, string script)
    {
        var result = await fixture.RunInWslAsync("set -euo pipefail\n" + script, TimeSpan.FromSeconds(10), inputViaStdin: true);
        Assert.False(result.TimedOut, "Selected-distro observation timed out.");
        Assert.Equal(0, result.ExitCode);
        return result.Stdout;
    }

    private static object SetupEvidence(string artifactDirectory)
    {
        var path = Path.Combine(artifactDirectory, "setup-engine.jsonl");
        var steps = new List<object>();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                foreach (Match match in Regex.Matches(line, "0x[0-9a-fA-F]{8}"))
                    if (codes.Count < 8) codes.Add(match.Value);
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                        !data.TryGetProperty("step_id", out var id) || !data.TryGetProperty("outcome", out var outcome)) continue;
                    var step = id.GetString() ?? "";
                    var result = outcome.GetString() ?? "";
                    if (Regex.IsMatch(step, "^[a-zA-Z0-9_.-]{1,80}$") && result is "Succeeded" or "Failed" or "Skipped" or "Success" or "Failure")
                        steps.Add(new { step, result });
                }
                catch (JsonException) { }
            }
        }
        return new { steps, hresults = codes.ToArray(), rawLogsRetainedPrivatelyOnly = true };
    }

    private static async Task WaitForInspection(Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static async Task<string> WindowsWslList()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode, CreateNoWindow = true
        };
        start.ArgumentList.Add("--list"); start.ArgumentList.Add("--quiet");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await WaitForInspection(process, TimeSpan.FromSeconds(10));
        await error;
        Assert.Equal(0, process.ExitCode);
        return await output;
    }
}
