using System.Security.AccessControl;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

[Collection("MxcOwnership")]
public sealed class MxcRuntimeNamespaceNativeTests
{
    [MxcNativeFact]
    public Task NativeSdk_AncestorDenyAllowsOnlyExecutorIssuedCodeAndScratchChildren() => ProbeAsync(false);

    [MxcNativeFact]
    public Task NativeBuilder_TwoConcurrentOperationsCannotAccessSiblingRuntimeUnderBroadWriteGrant() => ProbeAsync(true);

    [MxcNativeFact]
    public Task NativeSdk_InlinePayloadControlExercisesTheSameNamespaceAssertions() => ProbeAsync(false, inlinePayload: true);

    private static async Task ProbeAsync(bool productionPolicy, bool inlinePayload = false)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
            ".mxc-test-runs", Guid.NewGuid().ToString("N"));
        var context = MxcSyntheticContext.Create(root);
        var profile = context.RequireFolder("Profile");
        var runs = Directory.CreateDirectory(Path.Combine(profile, "OpenClawMxc", "Runs")).FullName;
        var sensitive = context.ProtectedPaths[0];
        context = context with { ExecutorPrivatePaths = [runs] };
        var operations = new[] { "first", "second" }.Select(name => Directory.CreateDirectory(Path.Combine(runs, name)).FullName).ToArray();
        foreach (var operation in operations)
        {
            Directory.CreateDirectory(Path.Combine(operation, "worker"));
            Directory.CreateDirectory(Path.Combine(operation, "scratch"));
            File.WriteAllText(Path.Combine(operation, "sentinel"), "sibling");
        }
        File.WriteAllText(Path.Combine(sensitive, "sentinel"), "sensitive");
        var aclPaths = operations.SelectMany(p => new[] { p, Path.Combine(p, "worker"), Path.Combine(p, "scratch") })
            .Concat([root, profile, runs, sensitive]).ToArray();
        var acls = aclPaths.ToDictionary(p => p, Acl);
        var count = productionPolicy ? 2 : 1;
        var readyCount = 0;
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var artifacts = Environment.GetEnvironmentVariable("OPENCLAW_MXC_PROOF_ROOT")!;
        Directory.CreateDirectory(artifacts);
        var captures = new MxcNamespaceProofCapture?[count];
        Exception? failure = null;
        Task? execution = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            await (execution = Task.WhenAll(Enumerable.Range(0, count).Select(ExecuteAsync)));
            Assert.All(operations, operation =>
            {
                Assert.Equal("sibling", File.ReadAllText(Path.Combine(operation, "sentinel")));
                Assert.False(File.Exists(Path.Combine(operation, "new")));
                Assert.False(File.Exists(Path.Combine(operation, "worker", "added.dll")));
            });
            Assert.Equal("sensitive", File.ReadAllText(Path.Combine(sensitive, "sentinel")));
            Assert.All(acls, pair => Assert.Equal(pair.Value, Acl(pair.Key)));
        }
        catch (Exception ex)
        {
            failure = execution?.Exception is { InnerExceptions.Count: > 1 } concurrentFailures ? concurrentFailures : ex;
        }
        foreach (var capture in captures.OfType<MxcNamespaceProofCapture>())
            cleanupFailures.AddRange(capture.RecordCase("before-fixture-cleanup", failure, cleanupFailures));
        try
        {
            if (captures.OfType<MxcNamespaceProofCapture>().Any(capture => !capture.Settled))
                throw new InvalidOperationException("Namespace process cleanup was not confirmed. Retaining the synthetic fixture: " + root);
            Directory.Delete(root, true);
        }
        catch (Exception ex) { cleanupFailures.Add(ex); }
        foreach (var capture in captures.OfType<MxcNamespaceProofCapture>())
            cleanupFailures.AddRange(capture.RecordCase("case-result", failure, cleanupFailures));
        MxcNamespaceProofCapture.ThrowFailures(failure, cleanupFailures);

        async Task ExecuteAsync(int index)
        {
            var code = Path.Combine(operations[index], "worker");
            var scratch = Path.Combine(operations[index], "scratch");
            var sibling = operations[1 - index];
            var script = Path.Combine(code, "probe.ps1");
            var scriptText = """
                param($Runs, $Sibling, $Sensitive, $Scratch, $Code)
                $ErrorActionPreference = 'Stop'
                [Console]::WriteLine('READY')
                if ([Console]::ReadLine() -ne 'go') { throw 'handshake' }
                function Denied($Name, [scriptblock]$Action) {
                    try { & $Action; throw "UNEXPECTED_ACCESS:$Name" }
                    catch [UnauthorizedAccessException] { [Console]::WriteLine("DENIED:$Name") }
                }
                [IO.File]::WriteAllText((Join-Path $Scratch 'own'), 'scratch')
                if ([IO.File]::ReadAllText((Join-Path $Scratch 'own')) -ne 'scratch') { throw 'scratch' }
                [Console]::WriteLine('OWN_SCRATCH_OK')
                Denied 'code-write' { [IO.File]::AppendAllText((Join-Path $Code 'probe.ps1'), 'bad') }
                Denied 'code-add' { [IO.File]::WriteAllText((Join-Path $Code 'added.dll'), 'bad') }
                Denied 'namespace-list' { [IO.Directory]::GetDirectories($Runs) }
                Denied 'sibling-read' { [IO.File]::ReadAllText((Join-Path $Sibling 'sentinel')) }
                Denied 'sibling-write' { [IO.File]::WriteAllText((Join-Path $Sibling 'sentinel'), 'bad') }
                Denied 'sibling-add' { [IO.File]::WriteAllText((Join-Path $Sibling 'new'), 'bad') }
                Denied 'sibling-code' { [IO.File]::WriteAllText((Join-Path $Sibling 'worker\added.dll'), 'bad') }
                Denied 'sibling-scratch' { [IO.File]::WriteAllText((Join-Path $Sibling 'scratch\new'), 'bad') }
                Denied 'protected-read' { [IO.File]::ReadAllText((Join-Path $Sensitive 'sentinel')) }
                Denied 'protected-write' { [IO.File]::WriteAllText((Join-Path $Sensitive 'sentinel'), 'bad') }
                """;
            File.WriteAllText(script, scriptText);
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var argv = BuildLaunchArguments(powershell, script, scriptText,
                [runs, sibling, sensitive, scratch, code], inlinePayload);
            ContainerRequest request;
            if (productionPolicy)
            {
                var settings = new SettingsData { SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadWrite };
                var resources = MxcRequestBuilder.ComposeResources(settings, sensitive, scratch, context,
                    MxcFolderAdmission.ReadMetadata, fixedWorkerDirectory: code);
                request = MxcRequestBuilder.CreateRequest(argv, scratch, settings, resources);
            }
            else request = new(MxcRequestBuilder.RenderArgv(argv))
            {
                Containment = new Containment.ProcessContainer { LearningMode = false },
                Filesystem = new()
                {
                    ReadonlyPaths = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), code],
                    ReadwritePaths = [profile, scratch], DeniedPaths = [runs, sensitive], ClearPolicyOnExit = true,
                },
                Network = new()
                {
                    Egress = new() { Default = NetworkAction.Deny },
                    Ingress = new() { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
                },
                Ui = new() { Disable = false, Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
                WorkingDirectory = scratch,
            };
            var name = $"namespace-{(productionPolicy ? "builder" : "sdk")}{(inlinePayload ? "-inline-control" : "")}";
            var capture = captures[index] = new(artifacts, name, index, new
            {
                RunId = Path.GetFileName(root), Root = root, Executable = powershell, Argv = argv,
                Request = request, Script = File.ReadAllText(script), Port = (int?)null,
                LaunchMode = inlinePayload ? "inline-control" : "file",
                ProductionPolicy = productionPolicy, ConcurrentProcesses = count,
            });
            await capture.RunAsync(async evidence =>
            {
                evidence.Phase = "probe admission";
                var probe = MxcContainer.Probe(request);
                evidence.Probe = probe;
                Assert.Null(probe.Error);
                Assert.Equal(IsolationTier.BaseContainer, probe.Tier);
                Assert.False(probe.NeedsDaclAugmentation);
                Assert.True(probe.Probes.BaseContainerSupportsDenyPaths);
                Assert.DoesNotContain(Path.GetPathRoot(root)!, request.Filesystem!.ReadonlyPaths);
                await evidence.StartAsync(async () => await MxcContainer.SpawnAsync(request,
                    new SpawnOptions { Telemetry = new() { Enabled = false } }, CancellationToken.None));
                Assert.Equal("READY", await evidence.ReadReadinessAsync());
                if (Interlocked.Increment(ref readyCount) == count) allReady.SetResult();
                evidence.Phase = "concurrent readiness";
                await allReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                evidence.Phase = "release handshake";
                using (var input = new StreamWriter(evidence.StandardInput)) { input.WriteLine("go"); input.Flush(); }
                var result = await evidence.FinishAsync();
                var output = evidence.Output;
                evidence.Phase = "behavior assertions";
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("OWN_SCRATCH_OK", output);
                foreach (var check in new[] { "code-write", "code-add", "namespace-list", "sibling-read", "sibling-write",
                             "sibling-add", "sibling-code", "sibling-scratch", "protected-read", "protected-write" })
                    Assert.Contains("DENIED:" + check, output);
            });
        }
    }

    internal static string[] BuildLaunchArguments(string powershell, string scriptPath, string scriptText,
        IReadOnlyList<string> parameters, bool inlinePayload)
    {
        if (!inlinePayload)
            return [powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath, .. parameters];

        // A separate control, never a fallback for the unchanged -File proof.
        // Encoding transports the same script and literal arguments without changing execution policy.
        var command = "& {\n" + scriptText + "\n} " +
            string.Join(" ", parameters.Select(value => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"));
        return [powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command))];
    }

    private static string Acl(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }
}
