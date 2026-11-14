# Tests the changed-test selection and soak runner settings used by the CI new-test soak.
# Run: pwsh -NoProfile -File .\scripts\test-ci-changed-tests.ps1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CiChangedTests.ps1')

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if (($Expected -join "`n") -cne ($Actual -join "`n")) {
        throw "$Message`nExpected: $($Expected -join ', ')`nActual:   $($Actual -join ', ')"
    }
}

# Strings, characters and comments can contain braces and attribute-like text without breaking parsing.
$tricky = @'
using Xunit;
namespace Sample.Tests;
public sealed class TrickyTests
{
    private const string Brace = "{ [Fact] }";
    private const string Verbatim = @"""{ [Theory] }""";
    private const string Raw = """
        { [Fact] }
        """;
    private static string Interpolated(int x) => $"{(x > 0 ? "{" : "}")} {x}";
    private const char Open = '{';
    // [Fact] public void Commented() { }
    /* [Fact] { */
    [Fact]
    public void First()
    {
        Assert.Equal("}", "}");
    }

    [Trait("Category", "Unit")]
    [Theory]
    [InlineData("{")]
    public void Second(string value) => Assert.NotNull(value);

    public sealed class Nested
    {
        [GatewayFixtureUiFact(Skip = "{")]
        public async Task Third() { await Task.Yield(); }
    }

    private static void Helper() { }
}
'@
$methods = Get-CiTestMethods -Source $tricky
Assert-Equal @('Sample.Tests.TrickyTests.First', 'Sample.Tests.TrickyTests.Second', 'Sample.Tests.TrickyTests+Nested.Third') `
    @($methods | ForEach-Object FullyQualifiedName) 'Test discovery must ignore strings, characters and comments.'
$second = $methods | Where-Object Method -EQ 'Second'
Assert-Equal @(20, 23) @($second.StartLine, $second.EndLine) 'A test span must start at attributes directly above it and end at its body.'

$blockNamespace = @'
namespace Outer.Space
{
    public class BlockTests
    {
        [Fact] public void Only() { }
    }
}
'@
Assert-Equal @('Outer.Space.BlockTests.Only') @((Get-CiTestMethods -Source $blockNamespace) | ForEach-Object FullyQualifiedName) 'Block namespaces must qualify tests.'

function Select-Lines([int[]]$Lines) {
    @((Select-CiChangedTests -Methods $methods -Source $tricky -ChangedLines $Lines) | ForEach-Object { "$($_.Kind):$($_.FullyQualifiedName)" })
}
Assert-Equal @('method:Sample.Tests.TrickyTests.First') (Select-Lines 17) 'A changed line inside a test selects that test.'
Assert-Equal @('method:Sample.Tests.TrickyTests.Second') (Select-Lines 20) 'A changed attribute selects its test.'
Assert-Equal @('class:Sample.Tests.TrickyTests+Nested.Third') (Select-Lines 26) 'A change elsewhere in a nested type selects that type''s tests.'
Assert-Equal @('class:Sample.Tests.TrickyTests.First', 'class:Sample.Tests.TrickyTests.Second', 'class:Sample.Tests.TrickyTests+Nested.Third') (Select-Lines 31) 'A helper change selects the tests of its type, including nested types.'
Assert-Equal @('file:Sample.Tests.TrickyTests.First', 'file:Sample.Tests.TrickyTests.Second', 'file:Sample.Tests.TrickyTests+Nested.Third') (Select-Lines 1) 'A change outside every type selects every test in the file.'
Assert-Equal @() (Select-Lines @(12, 13)) 'Comment-only lines select nothing.'
Assert-Equal @('method:Sample.Tests.TrickyTests.First', 'class:Sample.Tests.TrickyTests.Second', 'class:Sample.Tests.TrickyTests+Nested.Third') (Select-Lines @(17, 31)) 'A method hit outranks a fallback for the same test.'

# Edits that change behavior without adding code text still select the test.
$commented = $tricky.Replace('        Assert.Equal("}", "}");', '        // Assert.Equal("}", "}");')
Assert-Equal @('method:Sample.Tests.TrickyTests.First') `
    @((Select-CiChangedTests -Methods (Get-CiTestMethods -Source $commented) -Source $commented -ChangedLines @(17)) | ForEach-Object { "$($_.Kind):$($_.FullyQualifiedName)" }) `
    'Commenting out a line inside a test selects that test.'

$layouts = "namespace Layouts;`n" +
    "[Trait(`"Category`", `"Unit`"), Fact]`n" +
    "public class NotATest { }`n" +
    "public class Combined`n{`n" +
    "    [Trait(`"Category`", `"Unit`"), Fact]`n" +
    "    public void Listed() { }`n" +
    "    private static T Make<T, U>() where T : class`n" +
    "        where U : struct => default!;`n}`n" +
    "public class Outer`n{`n" +
    "    private static int Shared() => 1;`n" +
    "    public class Inner`n    {`n" +
    "        [Fact] public void Nested() { }`n    }`n}`n" +
    "internal sealed class FakeClock`n{`n    public int Now => 2;`n}`n"
$layoutMethods = Get-CiTestMethods -Source $layouts
Assert-Equal @('Layouts.Combined.Listed', 'Layouts.Outer+Inner.Nested') @($layoutMethods | ForEach-Object FullyQualifiedName) `
    'Combined attribute lists and generic constraints must parse.'
function Select-Layout([int[]]$Lines) {
    @((Select-CiChangedTests -Methods $layoutMethods -Source $layouts -ChangedLines $Lines) | ForEach-Object { "$($_.Kind):$($_.FullyQualifiedName)" })
}
Assert-Equal @('class:Layouts.Combined.Listed') (Select-Layout 9) 'A change in a constrained generic helper selects its type''s tests.'
Assert-Equal @('class:Layouts.Outer+Inner.Nested') (Select-Layout 13) 'An outer helper selects the tests of its nested types.'
Assert-Equal @('file:Layouts.Combined.Listed', 'file:Layouts.Outer+Inner.Nested') (Select-Layout 21) 'A helper type without tests selects the tests in its file.'

$noTests = "namespace Helpers;`npublic static class Support`n{`n    public static int Value => 1;`n}`n"
Assert-Equal @() @((Select-CiChangedTests -Methods (Get-CiTestMethods -Source $noTests) -Source $noTests -ChangedLines @(4))) 'A file without tests selects nothing.'

$diff = @'
diff --git a/tests/A.cs b/tests/A.cs
--- a/tests/A.cs
+++ b/tests/A.cs
@@ -3,0 +4,2 @@ class A
+x
+y
@@ -10 +12 @@
-old
+new
@@ -20,2 +21,0 @@
-gone
-gone
diff --git a/tests/B.cs b/tests/B.cs
deleted file mode 100644
--- a/tests/B.cs
+++ /dev/null
@@ -1 +0,0 @@
-x
'@
$map = Get-CiChangedLineMap -DiffText ($diff + "`ndiff --git a/tests/C.cs b/tests/C.cs`n--- a/tests/C.cs`n+++ b/tests/C.cs`n@@ -30 +30 @@`n-// old note`n+// new note`n@@ -40 +40 @@`n-    Value = 1;`n+    // Value = 1;")
Assert-Equal @('tests/A.cs', 'tests/C.cs') @($map.Keys | Sort-Object) 'Deleted files contribute no lines.'
Assert-Equal @(4, 5, 12, 21, 22) @($map['tests/A.cs'].Lines) 'New-side changed lines and the lines around a pure deletion are mapped.'
Assert-Equal @(12, 21, 22) @($map['tests/A.cs'].Forced | Sort-Object) 'Lines of hunks that removed code are forced.'
Assert-Equal @(40) @($map['tests/C.cs'].Forced) 'Editing a comment forces nothing; commenting out code does.'
$removals = Get-CiChangedLineMap -DiffText ("+++ b/D.cs`n@@ -5 +4,0 @@`n-    /* reset */ count++;`n" +
    "@@ -20,4 +18,0 @@`n-`n-    [Fact]`n-    public void Removed() { }`n-`n@@ -30,2 +27,0 @@`n-    /* old`n-     * note */")
Assert-Equal @(4, 5) @($removals['D.cs'].Forced | Sort-Object) 'Code after an inline comment is code; deleting a whole test or a comment forces nothing.'
$mixed = Get-CiChangedLineMap -DiffText ("+++ b/E.cs`n@@ -5,6 +4,0 @@`n-    public E() { count = 1; }`n-`n-    [Fact]`n-    public void Removed() { }`n-`n-    private const string Label = `"[Fact]`";`n" +
    "@@ -20 +14 @@`n-    */ count++;`n+    */")
Assert-Equal @(4, 5, 14) @($mixed['E.cs'].Forced | Sort-Object) 'Helper code removed next to a test, and code after a comment terminator, still force their lines.'
$onlyTests = Get-CiChangedLineMap -DiffText "+++ b/F.cs`n@@ -5,5 +4,0 @@`n-    [Fact]`n-    public void A() => Check(x =>`n-    {`n-    });`n-    // done"
Assert-Equal @() @($onlyTests['F.cs'].Forced) 'Removing only tests and comments forces nothing.'
$data = Get-CiChangedLineMap -DiffText "+++ b/G.cs`n@@ -9 +8,0 @@`n-        `"old value`""
Assert-Equal @(8, 9) @($data['G.cs'].Forced | Sort-Object) 'Removed string data, such as a fixture line, forces its lines.'
Assert-Equal @() (Select-Lines 30) 'An added blank line outside tests selects nothing.'
Assert-Equal @('class:Sample.Tests.TrickyTests.First', 'class:Sample.Tests.TrickyTests.Second', 'class:Sample.Tests.TrickyTests+Nested.Third') `
    @((Select-CiChangedTests -Methods $methods -Source $tricky -ChangedLines @(30) -ForcedLines @(30)) | ForEach-Object { "$($_.Kind):$($_.FullyQualifiedName)" }) `
    'A blank line left where helper code was removed selects the type''s tests.'

# Expression bodies end at their own semicolon, not one inside a lambda; attribute lists may hold whitespace.
$expression = "namespace Bodies;`npublic class BodyTests`n{`n" +
    "    [Fact]`n    public Task Lambda() => RunAsync(`"x`", root =>`n    {`n        var a = 1;`n        Assert.True(a == 1);`n    });`n`n" +
    "    [ Fact]`n    public void Spaced() { }`n`n" +
    "    [`n        Fact]`n    public void Split() { }`n`n" +
    "    [Theory]`n    [InlineData(`"x`")]`n    public void Constrained<T>(T value) where T : class`n    {`n        Assert.NotNull(value);`n    }`n}`n"
$expressionMethods = Get-CiTestMethods -Source $expression
Assert-Equal @('Bodies.BodyTests.Lambda:4-9', 'Bodies.BodyTests.Spaced:11-12', 'Bodies.BodyTests.Split:14-16', 'Bodies.BodyTests.Constrained:18-23') `
    @($expressionMethods | ForEach-Object { "$($_.FullyQualifiedName):$($_.StartLine)-$($_.EndLine)" }) 'Expression bodies, spaced attributes and constrained generic tests must parse.'
Assert-Equal @('method:Bodies.BodyTests.Lambda') `
    @((Select-CiChangedTests -Methods $expressionMethods -Source $expression -ChangedLines @(8)) | ForEach-Object { "$($_.Kind):$($_.FullyQualifiedName)" }) `
    'A change after a semicolon inside an expression-bodied test selects that test.'

# End-to-end selection against a temporary repository.
$repo = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-changed-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $repo 'tests\OpenClaw.Shared.Tests') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repo 'tests\Other.Tests') -Force | Out-Null
$savedMax = $env:OPENCLAW_CI_SOAK_MAX_TESTS
$savedOutput = $env:GITHUB_OUTPUT
try {
    Push-Location $repo
    & git init -q
    & git config user.email 'test@example.invalid'
    & git config user.name 'test'
    & git config core.autocrlf false
    $file = 'tests/OpenClaw.Shared.Tests/ProbeTests.cs'
    [IO.File]::WriteAllText((Join-Path $repo $file), "namespace Probe;`npublic class ProbeTests`n{`n    [Fact] public void Kept() { }`n}`n")
    [IO.File]::WriteAllText((Join-Path $repo 'tests/Other.Tests/OtherTests.cs'), "namespace Other;`npublic class OtherTests { }`n")
    & git add -A
    & git commit -q -m base
    $base = (& git rev-parse HEAD).Trim()
    [IO.File]::WriteAllText((Join-Path $repo $file), "namespace Probe;`npublic class ProbeTests`n{`n    [Fact] public void Kept() { }`n    [Fact] public void Added() { }`n    [Fact] public void AlsoAdded() { }`n}`n")
    [IO.File]::WriteAllText((Join-Path $repo 'tests/Other.Tests/OtherTests.cs'), "namespace Other;`npublic class OtherTests`n{`n    [Fact] public void NotSoaked() { }`n}`n")
    & git add -A
    & git commit -q -m change
    $selectionPath = Join-Path $repo 'selection.json'
    $env:GITHUB_OUTPUT = Join-Path $repo 'output.txt'
    $env:OPENCLAW_CI_SOAK_MAX_TESTS = '1'
    & (Join-Path $PSScriptRoot 'Get-CiChangedTests.ps1') -BaseRef $base -OutputPath $selectionPath | Out-Null
    $selection = Get-Content -LiteralPath $selectionPath -Raw | ConvertFrom-Json
    Assert-Equal @('shared') @($selection.projects | ForEach-Object name) 'Only soaked projects are selected.'
    Assert-Equal @('Probe.ProbeTests.Added') @($selection.projects[0].tests | ForEach-Object fullyQualifiedName) 'The cap keeps the first method-level hit.'
    Assert-Equal @('Probe.ProbeTests.AlsoAdded') @($selection.omitted | ForEach-Object fullyQualifiedName) 'Tests above the cap are reported as omitted.'
    Assert-Equal @('has_tests=true', 'ui_tests=false') @(Get-Content -LiteralPath $env:GITHUB_OUTPUT) 'The selection publishes its step outputs.'
    $env:OPENCLAW_CI_SOAK_MAX_TESTS = '0'
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Get-CiChangedTests.ps1') -BaseRef $base -OutputPath $selectionPath | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'An invalid OPENCLAW_CI_SOAK_MAX_TESTS must fail.' }
    $env:OPENCLAW_CI_SOAK_MAX_TESTS = $savedMax

    # UI tests are soaked only for method-level hits, under their own cap.
    $uiFile = 'tests/OpenClaw.Tray.UITests/WindowTests.cs'
    New-Item -ItemType Directory -Path (Join-Path $repo 'tests\OpenClaw.Tray.UITests') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo $uiFile), "namespace Ui;`npublic class WindowTests`n{`n    private static int Wait() => 1;`n    [Fact] public void Existing() { }`n}`n")
    & git add -A
    & git commit -q -m ui-base
    $uiBase = (& git rev-parse HEAD).Trim()
    [IO.File]::WriteAllText((Join-Path $repo $uiFile), "namespace Ui;`npublic class WindowTests`n{`n    private static int Wait() => 2;`n    [Fact] public void Existing() { }`n    [Fact] public void NewA() { }`n    [Fact] public void NewB() { }`n}`n")
    & git add -A
    & git commit -q -m ui-change
    Remove-Item -LiteralPath $env:GITHUB_OUTPUT
    $env:OPENCLAW_CI_SOAK_MAX_UI_TESTS = '1'
    & (Join-Path $PSScriptRoot 'Get-CiChangedTests.ps1') -BaseRef $uiBase -OutputPath $selectionPath | Out-Null
    $selection = Get-Content -LiteralPath $selectionPath -Raw | ConvertFrom-Json
    Assert-Equal @('Ui.WindowTests.NewA') @($selection.projects[0].tests | ForEach-Object fullyQualifiedName) 'UI soaks keep method-level hits up to the UI cap.'
    Assert-Equal @('Ui.WindowTests.NewB:cap', 'Ui.WindowTests.Existing:ui-fallback') @($selection.omitted | ForEach-Object { "$($_.fullyQualifiedName):$($_.reason)" }) `
        'UI fallbacks and tests above the UI cap are reported with a reason.'
    Assert-Equal @('has_tests=true', 'ui_tests=true') @(Get-Content -LiteralPath $env:GITHUB_OUTPUT) 'A UI selection publishes ui_tests.'
} finally {
    Pop-Location
    $env:OPENCLAW_CI_SOAK_MAX_UI_TESTS = $null
    $env:OPENCLAW_CI_SOAK_MAX_TESTS = $savedMax
    $env:GITHUB_OUTPUT = $savedOutput
    Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue
}

# Soak runner settings: invalid values fail before any test runs, and contended runs are spread out.
$runner = Join-Path $PSScriptRoot 'Invoke-CiTestSoak.ps1'
$emptySelection = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-empty-soak-" + [Guid]::NewGuid().ToString('N') + '.json')
'{"projects":[]}' | Set-Content -LiteralPath $emptySelection
try {
    foreach ($case in @(@{ OPENCLAW_CI_SOAK_ITERATIONS = '0' }, @{ OPENCLAW_CI_SOAK_ITERATIONS = 'x' }, @{ OPENCLAW_CI_SOAK_ITERATIONS = '2'; OPENCLAW_CI_SOAK_CONTENTION_ITERATIONS = '3' })) {
        $environment = ($case.GetEnumerator() | ForEach-Object { "`$env:$($_.Key) = '$($_.Value)'" }) -join '; '
        & pwsh -NoProfile -NonInteractive -Command "$environment; & '$runner' -SelectionPath '$emptySelection'" *> $null
        if ($LASTEXITCODE -eq 0) { throw "The soak runner accepted invalid settings: $environment" }
    }
    & pwsh -NoProfile -NonInteractive -Command "& '$runner' -SelectionPath '$emptySelection'" *> $null
    if ($LASTEXITCODE -ne 0) { throw 'The soak runner must succeed without selected tests.' }
} finally {
    Remove-Item -LiteralPath $emptySelection -ErrorAction SilentlyContinue
}
$runnerAst = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$null, [ref]$null)
$spread = $runnerAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-ContendedIteration' }, $true)
$contendedRuns = & ([scriptblock]::Create("`$iterations = 10; `$contentionIterations = 3; $($spread.Extent.Text); @(1..10 | Where-Object { Test-ContendedIteration `$_ })"))
Assert-Equal @(2, 5, 8) $contendedRuns 'Contended runs must be spread across the soak from its start.'
$budget = $runnerAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-SoakBudget' }, $true)
$budgetChecks = & ([scriptblock]::Create("`$stepTimeoutMinutes = 40; `$stepBudget = [pscustomobject]@{ Elapsed = [TimeSpan]::FromMinutes(30) }; $($budget.Extent.Text); " +
        "@((Test-SoakBudget 0 2160), (Test-SoakBudget 60 2160), (Test-SoakBudget 300 2160), (Test-SoakBudget 60 1850))"))
Assert-Equal @($false, $true, $false, $false) $budgetChecks 'Work starts only when 1.5 times its estimate, or a quarter budget without one, fits before the deadline.'

# Each subset runs with its CI lane's environment: fixture UI tests get native input only through the
# soak setting, and other UI tests run without the fixture or native-input opt-ins.
$subsetsAst = $runnerAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SoakSubsets' }, $true)
function Get-SubsetPlan([int]$NativeInput, [bool]$Ui) {
    (& ([scriptblock]::Create("`$fixtureNativeInput = $NativeInput; $($subsetsAst.Extent.Text); Get-SoakSubsets `$$Ui"))) | ForEach-Object {
        $environment = ($_.Environment.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$(if ($null -eq $_.Value) { '(unset)' } else { $_.Value })" }) -join ','
        "$($_.Name)|$($_.Filter)|$environment"
    }
}
Assert-Equal @('fixture-ui|&Category=GatewayFixture|OPENCLAW_RUN_GATEWAY_FIXTURE_UI=1,OPENCLAW_RUN_KEYBOARD_PROOF=1',
    'ui|&Category!=GatewayFixture|OPENCLAW_RUN_GATEWAY_FIXTURE_UI=(unset),OPENCLAW_RUN_KEYBOARD_PROOF=(unset)') (Get-SubsetPlan 1 $true) `
    'With native input enabled, only fixture UI tests get it.'
Assert-Equal 'fixture-ui|&Category=GatewayFixture|OPENCLAW_RUN_GATEWAY_FIXTURE_UI=1,OPENCLAW_RUN_KEYBOARD_PROOF=0' @(Get-SubsetPlan 0 $true)[0] `
    'Without the setting, fixture UI tests run without native input.'
Assert-Equal @('tests||') (Get-SubsetPlan 1 $false) 'Unit test projects run as one subset with the step environment.'
# A removed variable must be absent in child processes, not present and empty.
$setVariable = $runnerAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-SoakVariable' }, $true)
$childSees = & ([scriptblock]::Create("$($setVariable.Extent.Text); Set-SoakVariable 'OPENCLAW_SOAK_PROBE' 'x'; " +
        "`$set = & pwsh -NoProfile -NonInteractive -Command 'Test-Path Env:\OPENCLAW_SOAK_PROBE'; Set-SoakVariable 'OPENCLAW_SOAK_PROBE' `$null; " +
        "`$removed = & pwsh -NoProfile -NonInteractive -Command 'Test-Path Env:\OPENCLAW_SOAK_PROBE'; @(`$set, `$removed)"))
Assert-Equal @('True', 'False') @($childSees) 'Subset variables are set for child processes and truly removed afterwards.'
$nativeSelection = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-native-soak-" + [Guid]::NewGuid().ToString('N') + '.json')
'{"projects":[]}' | Set-Content -LiteralPath $nativeSelection
try {
    & pwsh -NoProfile -NonInteractive -Command "`$env:OPENCLAW_CI_SOAK_FIXTURE_NATIVE_INPUT = 'yes'; & '$runner' -SelectionPath '$nativeSelection'" *> $null
    if ($LASTEXITCODE -eq 0) { throw 'The soak runner accepted an invalid OPENCLAW_CI_SOAK_FIXTURE_NATIVE_INPUT.' }
} finally {
    Remove-Item -LiteralPath $nativeSelection -ErrorAction SilentlyContinue
}

Write-Host 'Changed-test selection and soak runner checks passed.'
