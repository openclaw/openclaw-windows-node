using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection;

namespace OpenClaw.E2ETests.Setup;

public sealed class BrowserWslPrototypeFactAttribute : FactAttribute
{
    public BrowserWslPrototypeFactAttribute()
    {
        if (!E2ETestGate.IsEnabled || Environment.GetEnvironmentVariable("OPENCLAW_BROWSER_WSL_PROTOTYPE") != "1")
            Skip = "Dedicated disposable user-systemd prototype only.";
    }
}

public sealed class BrowserWslOwnerPrototypeTests
{
    private static readonly string[] Cases = ["normal", "normal_setsid", "real_cli", "payload_capacity", "cancel", "eof", "deadline_setsid",
        "loss_before_ready", "loss_partial_permit", "loss_full_permit", "loss_after_result", "loss_after_settled",
        "late_start_job", "collision", "manager_epoch_replaced", "wrong_epoch", "duplicate_permit", "oversized", "out_of_order"];
    private readonly List<object> _cases = [];
    private readonly List<string> _errors = [];
    private string _source = "", _node = "";
    private long _gatewayPid;
    private bool _canonicalCapabilityVerified;

    [BrowserWslPrototypeFact]
    public async Task PrivateProtocolAndTransientScope_PrecedeProductionWiring()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.Equal("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"));
        var receipt = Environment.GetEnvironmentVariable("OPENCLAW_WSL_PROTOTYPE_RECEIPT")!;
        Assert.False(string.IsNullOrWhiteSpace(receipt));
        var fixture = new E2ESetupFixture();
        var stdout = Console.Out; var stderr = Console.Error;
        bool removed = false, gatewayUnchanged = false;
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
        try
        {
            await fixture.InitializeAsync(); Assert.Null(fixture.SetupError); await fixture.StopTrayAsync();
            _source = await File.ReadAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!, "scripts", "BrowserWslOwnerPrototype.cjs"));
            var preflight = await Guest(fixture, "export PATH=/home/openclaw/.openclaw/tools/node/bin:/usr/local/bin:/usr/bin:/bin; test \"$(id -un)\" = openclaw; command -v node; systemctl --user show openclaw-gateway.service -p MainPID --value");
            var lines = preflight.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _node = lines[0]; _gatewayPid = long.Parse(lines[1]);
            Assert.StartsWith("/", _node); Assert.True(_gatewayPid > 1);
            // The public release CLI can predate this consumer contract. Never substitute it silently.
            var capability = await Guest(fixture, "export PATH=/home/openclaw/.openclaw/tools/node/bin:/usr/local/bin:/usr/bin:/bin; for cli in /home/openclaw/.openclaw/bin/openclaw /opt/openclaw/bin/openclaw /usr/local/bin/openclaw; do if test -x \"$cli\"; then if \"$cli\" browser extension pair --help 2>/dev/null | grep -F -- --local-gateway >/dev/null; then printf capability_verified; exit 0; fi; exit 1; fi; done; exit 1");
            Assert.Equal("capability_verified", capability.Trim()); _canonicalCapabilityVerified = true;
            foreach (var name in Cases)
            {
                try { await RunCase(fixture, name); }
                catch (Exception ex) { _errors.Add(name + ":" + ex.GetType().Name); }
            }
            gatewayUnchanged = (await Guest(fixture, "systemctl --user show openclaw-gateway.service -p MainPID --value")).Trim() == _gatewayPid.ToString();
        }
        catch (Exception ex) { _errors.Add("fixture:" + ex.GetType().Name); }
        finally
        {
            try { await fixture.DisposeAsync(); removed = true; }
            catch (Exception ex) { _errors.Add("dispose:" + ex.GetType().Name); }
            Console.SetOut(stdout); Console.SetError(stderr);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                schema = 1, sourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                prototypeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_source))).ToLowerInvariant(),
                consumerSha = Environment.GetEnvironmentVariable("OPENCLAW_WSL_PROTOTYPE_CONSUMER_SHA"),
                candidatePackageSha256 = Environment.GetEnvironmentVariable("OPENCLAW_WSL_PROTOTYPE_PACKAGE_SHA256"),
                candidateVersion = Environment.GetEnvironmentVariable("OPENCLAW_E2E_GATEWAY_VERSION"),
                canonicalCapabilityVerified = _canonicalCapabilityVerified,
                productionUnchanged = true, publicProtocolUnchanged = true, scope = "private protocol/systemd prototype, not production owner repair or whole-Companion proof",
                cases = _cases, errors = _errors, gatewayUnchanged, ownedFixtureDisposed = removed,
                payloadLimitUtf16 = 16384, privateResultFrameBound = 90000,
                status = _errors.Count == 0 && _cases.Count == Cases.Length && removed && gatewayUnchanged ? "prototype_checkpoint_passed" : "prototype_incomplete"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.Empty(_errors); Assert.Equal(Cases.Length, _cases.Count); Assert.True(removed && gatewayUnchanged);
    }

    private async Task RunCase(E2ESetupFixture fixture, string name)
    {
        var id = Guid.NewGuid().ToString("N"); var unit = "openclaw-browser-prototype-" + id + ".service";
        var description = "OpenClaw browser prototype " + id;
        var hold = name is "deadline_setsid" or "loss_full_permit" or "duplicate_permit";
        var workload = hold
            ? "const{spawn}=require('node:child_process');spawn(process.execPath,['-e','setTimeout(()=>{},60000)'],{detached:true,stdio:'ignore'});setTimeout(()=>{},60000);"
            : name == "normal_setsid" ? "const fs=require('node:fs');const p=require('node:child_process').spawn(process.execPath,['-e','setTimeout(()=>{},60000)'],{detached:true,stdio:'ignore'});p.unref();const a=fs.readFileSync('/proc/'+p.pid+'/stat','utf8').split(') ').at(-1).split(' ');process.stdout.write(JSON.stringify({pid:p.pid,start:Number(a[19]),session:Number(a[3]),sameCgroup:fs.readFileSync('/proc/'+p.pid+'/cgroup','utf8')===fs.readFileSync('/proc/self/cgroup','utf8')}));"
            : name == "payload_capacity" ? "process.stdout.write('界'.repeat(16384));" : "process.stdout.write(JSON.stringify({prototype:true}));";
        var settings = new { mode = "broker", requestId = id, node = _node, realCli = name == "real_cli",
            command = name == "real_cli" ? new[] { "selected-cli", "browser", "extension", "pair", "--json", "--local-gateway" } : new[] { _node, "-e", workload },
            readyDelayMs = name == "loss_before_ready" ? 2000 : 0, startDelayMs = name == "late_start_job" ? 1000 : 0,
            settledDelayMs = name == "loss_after_result" ? 500 : 0, stopDelayMs = name == "manager_epoch_replaced" ? 1500 : 0 };
        var source64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(_source));
        var js = "const settings=" + JsonSerializer.Serialize(settings) + ";if(settings.realCli)settings.command[0]=process.argv[1];const prototypeSource='" + source64 + "';" + _source;
        var program64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(js));
        var prefix = BrowserBootstrapWslCommand.Script[..BrowserBootstrapWslCommand.Script.IndexOf("for cli in", StringComparison.Ordinal)];
        var script = prefix + "\ncli=''; for candidate in /home/openclaw/.openclaw/bin/openclaw /opt/openclaw/bin/openclaw /usr/local/bin/openclaw; do if test -x \"$candidate\"; then cli=\"$candidate\"; break; fi; done\ntest -n \"$cli\"\nexec '" + _node + "' -e \"$(printf '%s' '" + program64 + "' | base64 -d)\" \"$cli\"\n";
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var clock = Stopwatch.StartNew();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--distribution", fixture.DistroName, "--exec", "/bin/bash", "--noprofile", "--norc", "-s" }) start.ArgumentList.Add(arg);
        start.Environment.Remove("WSLENV");
        bool collision = name == "collision", settled = false, resultSeen = false, canonicalValid = false, capacityPreserved = false, stopped = false;
        bool beforeReadyLoss = name is "loss_before_ready" or "late_start_job";
        JsonElement? ready = null; long? readyMs = null, permitMs = null, resultMs = null, settledMs = null;
        string? failure = null, outcome = null; int processCountBeforeLoss = 0; bool setsidObserved = false, epochRefused = false;
        bool replacement = name == "manager_epoch_replaced";
        object? normalDetachedIdentity = null;
        string? collisionInvocation = null;
        if (collision)
        {
            await Guest(fixture, $"systemd-run --user --quiet --unit='{unit}' --property='Description=Fixture collision {id}' /bin/sleep 60");
            collisionInvocation = (await Probe(fixture, unit)).GetProperty("invocation").GetString();
        }
        using var process = Process.Start(start)!;
        var errors = Drain(process.StandardError.BaseStream);
        try
        {
            script = NormalizeInput(script);
            Assert.DoesNotContain("\r", script);
            await process.StandardInput.WriteAsync(script.AsMemory(), budget.Token);
            await process.StandardInput.FlushAsync(budget.Token); // Intentionally keep stdin open across bash -> inline broker.
            if (beforeReadyLoss)
            {
                await WaitUnit(fixture, unit, p => p.GetProperty("present").GetBoolean(), budget.Token);
                process.Kill();
            }
            else if (!collision)
            {
                ready = await ReadFrame(process.StandardOutput.BaseStream, budget.Token);
                Assert.Equal("ready", ready.Value.GetProperty("type").GetString());
                Assert.Equal(id, ready.Value.GetProperty("requestId").GetString()); Assert.Equal(unit, ready.Value.GetProperty("unit").GetString());
                Assert.True(ready.Value.GetProperty("environmentClean").GetBoolean());
                readyMs = clock.ElapsedMilliseconds;
                var invocation = ready.Value.GetProperty("invocationId").GetString()!;
                var permit = new { v = 1, type = "permit", requestId = id, invocationId = invocation, challenge = ready.Value.GetProperty("challenge").GetString() };
                var bytes = Encode(permit);
                if (replacement)
                {
                    await Write(process, Encode(new { v = 1, type = "cancel", requestId = id, invocationId = invocation }), budget.Token);
                    await Guest(fixture, $"systemctl --user stop '{unit}'; systemd-run --user --quiet --unit='{unit}' --property='Description=Fixture epoch {id}' /bin/sleep 60");
                }
                else if (name == "cancel") await Write(process, Encode(new { v = 1, type = "cancel", requestId = id, invocationId = invocation }), budget.Token);
                else if (name == "eof") process.StandardInput.Close();
                else if (name == "wrong_epoch") await Write(process, Encode(new { v = 1, type = "permit", requestId = id, invocationId = new string('0', 32), challenge = permit.challenge }), budget.Token);
                else if (name == "out_of_order") await Write(process, Encode(new { v = 1, type = "result", requestId = id, invocationId = invocation, payload = "" }), budget.Token);
                else if (name == "oversized") { var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, 1000000); await Write(process, header, budget.Token); }
                else if (name == "loss_partial_permit") { await Write(process, bytes[..(bytes.Length / 2)], budget.Token); process.Kill(); }
                else
                {
                    permitMs = clock.ElapsedMilliseconds; await Write(process, bytes, budget.Token);
                    if (hold)
                    {
                        var observed = await WaitUnit(fixture, unit, p => p.GetProperty("processes").GetArrayLength() >= 3, budget.Token);
                        processCountBeforeLoss = observed.GetProperty("processes").GetArrayLength();
                        setsidObserved = observed.GetProperty("processes").EnumerateArray().Select(p => p.GetProperty("session").GetInt32()).Distinct().Count() >= 2;
                        Assert.True(setsidObserved);
                    }
                    if (name == "loss_full_permit") process.Kill();
                    if (name == "duplicate_permit") await Write(process, bytes, budget.Token);
                    if (name == "deadline_setsid")
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, 15000 - clock.ElapsedMilliseconds)), budget.Token);
                        process.StandardInput.Close();
                    }
                }
                while (!process.HasExited || process.StandardOutput.BaseStream.CanRead)
                {
                    JsonElement message;
                    try { message = await ReadFrame(process.StandardOutput.BaseStream, budget.Token); }
                    catch (EndOfStreamException) { break; }
                    Assert.Equal(id, message.GetProperty("requestId").GetString()); Assert.Equal(invocation, message.GetProperty("invocationId").GetString());
                    switch (message.GetProperty("type").GetString())
                    {
                        case "result":
                            Assert.False(settled); Assert.False(resultSeen); resultSeen = true; resultMs = clock.ElapsedMilliseconds;
                            var payload = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(message.GetProperty("payload").GetString()!));
                            Assert.True(payload.Length <= 16384);
                            if (name == "real_cli") { _ = BrowserBootstrapService.ParsePairing(payload, fixture.GatewayPort); canonicalValid = true; }
                            if (name == "payload_capacity") { Assert.Equal(new string('界', 16384), payload); capacityPreserved = true; }
                            if (name == "normal_setsid")
                            {
                                using var child = JsonDocument.Parse(payload); var d = child.RootElement;
                                Assert.Equal(d.GetProperty("pid").GetInt32(), d.GetProperty("session").GetInt32());
                                Assert.True(d.GetProperty("sameCgroup").GetBoolean()); setsidObserved = true;
                                normalDetachedIdentity = new { pid = d.GetProperty("pid").GetInt32(), start = d.GetProperty("start").GetInt64() };
                            }
                            if (name == "loss_after_result" && !process.HasExited) process.Kill();
                            break;
                        case "settled":
                            Assert.False(settled);
                            outcome = message.GetProperty("outcome").GetString();
                            Assert.Contains(outcome, new[] { "completed", "cancelled", "failed" });
                            if (outcome == "completed") Assert.True(resultSeen);
                            settled = true; settledMs = clock.ElapsedMilliseconds;
                            foreach (var key in new[] { "startSealed", "gateExited", "cgroupEmpty", "startJobSettled" }) Assert.True(message.GetProperty(key).GetBoolean());
                            if (name == "loss_after_settled" && !process.HasExited) process.Kill();
                            break;
                        default: throw new InvalidDataException("Unexpected prototype frame.");
                    }
                }
            }
            await process.WaitForExitAsync(budget.Token);
            var brokerStarted = await errors.WaitAsync(budget.Token);
            Assert.True(brokerStarted, "Prototype broker entry not observed.");
            var post = await WaitUnit(fixture, unit, p => collision || replacement ? p.GetProperty("present").GetBoolean() : p.GetProperty("processes").GetArrayLength() == 0, budget.Token);
            if (collision || replacement) Assert.False(post.GetProperty("owned").GetBoolean());
            if (collision)
            {
                Assert.Equal(43, process.ExitCode);
                Assert.Equal(collisionInvocation, post.GetProperty("invocation").GetString());
            }
            if (replacement)
            {
                Assert.NotEqual(ready!.Value.GetProperty("invocationId").GetString(), post.GetProperty("invocation").GetString());
                Assert.Equal("Fixture epoch " + id, post.GetProperty("description").GetString());
                Assert.True(post.GetProperty("processes").GetArrayLength() > 0); epochRefused = true;
            }
            var expectedUnknown = name.StartsWith("loss_", StringComparison.Ordinal) && name != "loss_after_settled" || name is "late_start_job" or "collision" or "manager_epoch_replaced";
            Assert.Equal(!expectedUnknown, settled);
            if (settled) Assert.True(settledMs <= 17000, "Prototype exceeded unchanged request plus soft cleanup budget.");
            if (name is "cancel" or "eof" or "deadline_setsid" or "wrong_epoch" or "duplicate_permit" or "oversized" or "out_of_order")
            {
                Assert.False(resultSeen); Assert.Equal("cancelled", outcome);
            }
            if (name == "real_cli") Assert.True(canonicalValid);
            if (name == "payload_capacity") Assert.True(capacityPreserved);
            _cases.Add(new { name, requestId = id, windowsPid = process.Id, readyMs, permitMs, resultMs, settledMs,
                stdinHandoffVerified = ready.HasValue, scriptInputNormalized = true, brokerStarted, resultSeen, resultReleased = settled && resultSeen && outcome == "completed", outcome,
                positiveSettlement = settled, retirementAllowed = settled, expectedUnknown,
                classification = settled ? "positive_settlement" : "UNKNOWN_BUSY_no_ack_no_retirement",
                canonicalValid, capacityPreserved, processCountBeforeLoss, setsidObserved, normalDetachedIdentity,
                postQueryEmpty = post.GetProperty("processes").GetArrayLength() == 0, collisionRefused = collision,
                protocolCasePassed = true, prototypeOnly = true, windowsProcessAndDrainsJoined = true,
                submissionFenceProven = false, observedEpochDriftRefused = epochRefused, atomicManagerReplacementFenceProven = false, actualProductionRetirementExercised = false });
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name;
            _cases.Add(new { name, protocolCasePassed = false, failureType = failure,
                processExited = process.HasExited, exitCode = process.HasExited ? (int?)process.ExitCode : null,
                readySeen = ready.HasValue, brokerStarted = errors.IsCompletedSuccessfully && errors.Result, resultSeen, positiveSettlement = settled, retirementAllowed = false,
                prototypeOnly = true });
            throw;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            var post = await Probe(fixture, unit);
            if (post.GetProperty("present").GetBoolean())
            {
                var expectedDescription = collision ? "Fixture collision " + id : replacement ? "Fixture epoch " + id : description;
                Assert.Equal(expectedDescription, post.GetProperty("description").GetString());
                await Guest(fixture, $"systemctl --user stop '{unit}'");
            }
            stopped = (await Probe(fixture, unit)).GetProperty("processes").GetArrayLength() == 0;
            Assert.True(stopped);
            try { await errors.WaitAsync(TimeSpan.FromSeconds(3)); } catch when (failure is not null) { }
        }
    }

    private static byte[] Encode(object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value); var bytes = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, body.Length); body.CopyTo(bytes, 4); return bytes;
    }
    private static async Task Write(Process process, byte[] bytes, CancellationToken ct)
    { await process.StandardInput.BaseStream.WriteAsync(bytes, ct); await process.StandardInput.BaseStream.FlushAsync(ct); }
    private static async Task<JsonElement> ReadFrame(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header); if (size is 0 or > 90000) throw new InvalidDataException();
        var bytes = new byte[(int)size]; await stream.ReadExactlyAsync(bytes, ct);
        _ = new UTF8Encoding(false, true).GetString(bytes);
        using var doc = JsonDocument.Parse(bytes); var value = doc.RootElement;
        Assert.Equal(value.EnumerateObject().Count(), value.EnumerateObject().Select(p => p.Name).Distinct().Count());
        Assert.Equal(1, value.GetProperty("v").GetInt32());
        var type = value.GetProperty("type").GetString();
        var expected = type switch
        {
            "ready" => "v,type,requestId,invocationId,challenge,unit,bootId,pid,start,cgroup,environmentClean",
            "result" => "v,type,requestId,invocationId,payload",
            "settled" => "v,type,requestId,invocationId,outcome,startSealed,gateExited,cgroupEmpty,startJobSettled",
            _ => throw new InvalidDataException("Unknown prototype frame.")
        };
        Assert.Equal(expected.Split(',').Order(), value.EnumerateObject().Select(p => p.Name).Order());
        if (type != "result" && size > 2048) throw new InvalidDataException("Control frame bound.");
        Assert.Matches("^[a-f0-9]{32}$", value.GetProperty("requestId").GetString()!);
        Assert.Matches("^[a-f0-9]{32}$", value.GetProperty("invocationId").GetString()!);
        return value.Clone();
    }
    internal static string NormalizeInput(string script) => script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n') + "\n";
    private static async Task<bool> Drain(Stream stream)
    {
        var bytes = new byte[1024]; var total = 0; var text = new StringBuilder(); int read;
        while ((read = await stream.ReadAsync(bytes)) != 0)
        {
            if ((total += read) > 8192) throw new InvalidDataException();
            text.Append(Encoding.UTF8.GetString(bytes, 0, read));
        }
        // Only this fixed marker leaves the private error drain. Never retain raw stderr.
        return text.ToString().Split('\n').Contains("OC_PROTO_BROKER_STARTED", StringComparer.Ordinal);
    }
    private static async Task<string> Guest(E2ESetupFixture fixture, string script)
    {
        var result = await fixture.RunInWslAsync("set -euo pipefail\n" + script, TimeSpan.FromSeconds(10), inputViaStdin: true);
        Assert.False(result.TimedOut); Assert.Equal(0, result.ExitCode); return result.Stdout;
    }
    private static async Task<JsonElement> WaitUnit(E2ESetupFixture fixture, string unit, Func<JsonElement, bool> predicate, CancellationToken ct)
    {
        var until = Stopwatch.StartNew();
        do { var observed = await Probe(fixture, unit); if (predicate(observed)) return observed; await Task.Delay(50, ct); }
        while (until.Elapsed < TimeSpan.FromSeconds(10));
        throw new TimeoutException("Prototype unit observation.");
    }
    private static async Task<JsonElement> Probe(E2ESetupFixture fixture, string unit)
    {
        Assert.Matches("^openclaw-browser-prototype-[a-f0-9]{32}\\.service$", unit);
        var code = "import subprocess,json,pathlib\nu='" + unit + "'\ns=subprocess.run(['systemctl','--user','show',u,'-p','LoadState','-p','Description','-p','ControlGroup','-p','InvocationID'],capture_output=True,text=True)\nv=dict(l.split('=',1) for l in s.stdout.splitlines() if '=' in l)\ng=v.get('ControlGroup','')\np=[]\nif g.startswith('/user.slice/') and '..' not in g:\n f=pathlib.Path('/sys/fs/cgroup'+g+'/cgroup.procs')\n if f.exists():\n  for pid in f.read_text().split():\n   try:\n    a=pathlib.Path('/proc/'+pid+'/stat').read_text().rsplit(') ',1)[1].split();p.append({'pid':int(pid),'start':int(a[19]),'session':int(a[3])})\n   except FileNotFoundError:pass\nprint(json.dumps({'present':v.get('LoadState')=='loaded','owned':v.get('Description','').startswith('OpenClaw browser prototype '),'description':v.get('Description',''),'invocation':v.get('InvocationID',''),'processes':p}))";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
        using var doc = JsonDocument.Parse(await Guest(fixture, $"printf '%s' '{b64}' | base64 -d | /usr/bin/python3")); return doc.RootElement.Clone();
    }
}
