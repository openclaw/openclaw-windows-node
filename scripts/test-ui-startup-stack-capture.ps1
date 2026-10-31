<#
.SYNOPSIS
    Exercises startup stack capture against an owned synthetic managed testhost.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StackTool
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "openclaw-stack-check-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$project = Join-Path $root 'probe.csproj'
$target = $null
try {
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>testhost</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $project
    @'
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

if (args[0] == "report")
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}
if (args[0] == "proxy")
{
    var child = new ProcessStartInfo(args[1]) { UseShellExecute = false };
    foreach (var argument in args.Skip(2)) child.ArgumentList.Add(argument);
    using var process = Process.Start(child)!;
    await process.WaitForExitAsync();
    return process.ExitCode;
}

using var release = new ManualResetEventSlim();
var thread = new Thread(() => WaitForDiagnosticRelease(release)) { IsBackground = true };
thread.Start();
using var current = Process.GetCurrentProcess();
var helper = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true };
foreach (var value in new[] {
    "-NoProfile", "-NonInteractive", "-File", args[1],
    "-TesthostId", current.Id.ToString(CultureInfo.InvariantCulture),
    "-ExpectedStartTicks", (current.StartTime.ToUniversalTime().Ticks + (args[4] == "stale" ? 1 : 0)).ToString(CultureInfo.InvariantCulture),
    "-Phase", "synthetic blocked thread", "-OutputDirectory", args[3],
    "-StackTool", args[2], "-CaptureTimeoutSeconds", args[4] == "timeout" ? "1" : "30"
}) helper.ArgumentList.Add(value);
if (args[4] == "parent")
{
    var proxy = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    proxy.ArgumentList.Add("proxy");
    proxy.ArgumentList.Add(helper.FileName);
    foreach (var value in helper.ArgumentList) proxy.ArgumentList.Add(value);
    helper = proxy;
}
using var collector = Process.Start(helper)!;
try
{
    await collector.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
    File.WriteAllText(Path.Combine(args[3], "target.json"),
        JsonSerializer.Serialize(new { helperExitCode = collector.ExitCode, stillAlive = true }));
}
finally
{
    if (!collector.HasExited) collector.Kill(entireProcessTree: true);
    release.Set();
    thread.Join();
}
// The diagnostic outcome must never replace the target's original failure.
return 42;

static void WaitForDiagnosticRelease(ManualResetEventSlim release) => release.Wait();
'@ | Set-Content -LiteralPath (Join-Path $root 'Program.cs')
    & dotnet build $project --verbosity quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic diagnostic target failed to build.' }
    $executable = Join-Path $root 'bin\Debug\net10.0\testhost.exe'
    $shell = (Get-Process -Id $PID).Path
    $capture = Join-Path $PSScriptRoot 'Capture-UiStartupStacks.ps1'
    $dotnet = (Get-Command dotnet).Source
    $cases = @(
        @{ name = 'capture'; tool = $StackTool; expected = 'captured' },
        @{ name = 'stale'; tool = $StackTool; expected = 'identity-rejected' },
        @{ name = 'parent'; tool = $StackTool; expected = 'identity-rejected' },
        @{ name = 'failure'; tool = $dotnet; expected = 'tool-failed' },
        @{ name = 'timeout'; tool = $executable; expected = 'capture-timeout' }
    )
    foreach ($case in $cases) {
        $output = Join-Path $root $case.name
        New-Item -ItemType Directory -Path $output | Out-Null
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($argument in @($shell, $capture, $case.tool, $output, $case.name)) {
            $start.ArgumentList.Add($argument)
        }
        $target = [Diagnostics.Process]::Start($start)
        if (-not $target.WaitForExit(60000)) {
            $target.Kill($true)
            $target.WaitForExit()
            throw "Diagnostic check '$($case.name)' exceeded its bound."
        }
        if ($target.ExitCode -ne 42) { throw "Diagnostic check '$($case.name)' changed the target failure." }
        $receiptFile = @(Get-ChildItem -LiteralPath $output -Filter capture.json -Recurse)
        if ($receiptFile.Count -ne 1) { throw 'Expected exactly one capture receipt.' }
        $receipt = Get-Content -LiteralPath $receiptFile[0].FullName -Raw | ConvertFrom-Json
        if ($receipt.status -cne $case.expected) {
            throw "Diagnostic check '$($case.name)' expected '$($case.expected)', got '$($receipt.status)'."
        }
        $targetResult = Get-Content -LiteralPath (Join-Path $output 'target.json') -Raw | ConvertFrom-Json
        if (-not $targetResult.stillAlive -or
            (($targetResult.helperExitCode -eq 0) -ne ($case.name -eq 'capture'))) {
            throw "Diagnostic check '$($case.name)' lost its independent capture result."
        }
        $stacks = Join-Path $receiptFile[0].DirectoryName 'managed-stacks.txt'
        if ($case.name -eq 'capture' -and
            (Get-Content -LiteralPath $stacks -Raw) -notmatch 'WaitForDiagnosticRelease') {
            throw 'The real stack tool did not report the intentionally blocked method.'
        }
        if ($case.expected -eq 'identity-rejected' -and (Test-Path -LiteralPath $stacks)) {
            throw 'Identity rejection must happen before stack collection.'
        }
        Write-Host "PASS $($case.name)"
        $target.Dispose()
        $target = $null
    }
    Write-Host 'UI startup stack capture: 5 checks passed.'
} finally {
    if ($null -ne $target) {
        if (-not $target.HasExited) { $target.Kill($true); $target.WaitForExit() }
        $target.Dispose()
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
