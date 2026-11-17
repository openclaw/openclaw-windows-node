$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-ReactorPackages.ps1')
$directory = Join-Path ([IO.Path]::GetTempPath()) ('reactor-bootstrap-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$script:downloads = 0
$script:downloadMode = 'valid'
function Invoke-WebRequest {
    param($Uri, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
    $script:downloads++
    if ($script:downloadMode -eq 'error') { throw 'Synthetic network failure.' }
    [IO.File]::WriteAllText($OutFile, $(if ($script:downloadMode -eq 'valid') { 'official fixture' } else { 'wrong bytes' }))
}
$passed = 0
try {
    $fixture = Join-Path $directory 'fixture'
    [IO.File]::WriteAllText($fixture, 'official fixture')
    $hash = (Get-FileHash $fixture -Algorithm SHA256).Hash
    $target = Join-Path $directory 'package.nupkg'
    Save-VerifiedReactorPackage 'https://example.test/package' $hash $target
    if ((Get-FileHash $target).Hash -ne $hash -or $downloads -ne 1) { throw 'Verified download was not published.' }
    $passed++
    Save-VerifiedReactorPackage 'https://example.test/package' $hash $target
    if ($downloads -ne 1) { throw 'Verified cached package downloaded again.' }
    $passed++
    [IO.File]::WriteAllText($target, 'corrupt cache')
    $failed = $false
    try { Save-VerifiedReactorPackage 'https://example.test/package' $hash $target }
    catch { $failed = $_.ToString().Contains('Cached Reactor package digest mismatch') }
    if (!$failed -or $downloads -ne 1) { throw 'Corrupt cache was accepted or silently replaced.' }
    $passed++
    foreach ($mode in 'invalid', 'error') {
        $script:downloadMode = $mode
        $target = Join-Path $directory "$mode.nupkg"
        $failed = $false
        try { Save-VerifiedReactorPackage 'https://example.test/package' $hash $target }
        catch { $failed = $_.ToString().Contains($(if ($mode -eq 'invalid') { 'Downloaded Reactor package digest mismatch' } else { 'Synthetic network failure' })) }
        if (!$failed -or (Test-Path $target) -or @(Get-ChildItem $directory -Filter '*.tmp').Count) {
            throw 'Invalid/failed acquisition published bytes or retained its temporary file.'
        }
        $passed++
    }
    [xml]$config = Get-Content (Join-Path $PSScriptRoot '..\NuGet.Config') -Raw
    $source = @($config.configuration.packageSources.add | Where-Object key -eq 'reactor-release')
    $mapping = @($config.configuration.packageSourceMapping.packageSource | Where-Object key -eq 'reactor-release')
    if ($source.Count -ne 1 -or $source[0].value -ne 'artifacts/reactor-packages' -or
        @($mapping.package.pattern).Count -ne 2 -or
        'Microsoft.UI.Reactor' -notin @($mapping.package.pattern) -or
        'Microsoft.UI.Reactor.Advanced' -notin @($mapping.package.pattern)) {
        throw 'Portable release feed mapping is missing or broadened.'
    }
    $passed++
    $scriptDirectory = Join-Path $directory 'scripts'
    [IO.Directory]::CreateDirectory($scriptDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $scriptDirectory 'Initialize-ReactorPackages.ps1'), "throw 'Synthetic bootstrap caller failure.'")
    $script:dotnetReached = $false
    function Invoke-DotNetCaptured { param($Arguments) $script:dotnetReached = $true }
    $repo = Split-Path $PSScriptRoot -Parent
    $parseErrors = $null
    $buildAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'build.ps1'), [ref]$null, [ref]$parseErrors)
    if ($parseErrors) { throw $parseErrors }
    $builder = $buildAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Build-Project'
    }, $true)
    . ([scriptblock]::Create($builder.Extent.Text))
    $repoRoot = $directory
    $Configuration = 'Debug'
    $DevBuild = $false
    $failed = $false
    try { Build-Project 'WinUI' $fixture }
    catch { $failed = $_.ToString().Contains('Synthetic bootstrap caller failure') }
    if (!$failed -or $dotnetReached) { throw 'Normal build overwrote bootstrap failure with a later dotnet result.' }
    $passed++
    $launcherAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repo 'run-app-local.ps1'), [ref]$null, [ref]$parseErrors)
    if ($parseErrors) { throw $parseErrors }
    $localBuild = @($launcherAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -eq '-not $NoBuild'
    }, $true))
    if ($localBuild.Count -ne 1) { throw 'Expected one local-launcher build boundary.' }
    [IO.File]::WriteAllText((Join-Path $scriptDirectory 'Initialize-ReactorPackages.ps1'),
        '$reactorCalls.Add("bootstrap"); if ($failBootstrap) { throw "Synthetic bootstrap caller failure." }')
    function dotnet { $reactorCalls.Add('dotnet'); $global:LASTEXITCODE = 0 }
    $projectPath = $fixture
    $RuntimeIdentifier = 'win-arm64'
    $Dev = $false
    foreach ($case in @(
        @{ NoBuild = $false; Failure = $false; Calls = 'bootstrap,dotnet'; Throws = $false },
        @{ NoBuild = $false; Failure = $true; Calls = 'bootstrap'; Throws = $true },
        @{ NoBuild = $true; Failure = $false; Calls = ''; Throws = $false },
        @{ NoBuild = $true; Failure = $true; Calls = ''; Throws = $false }
    )) {
        $NoBuild = $case.NoBuild
        $failBootstrap = $case.Failure
        $reactorCalls = [Collections.Generic.List[string]]::new()
        $failed = $false
        try { & ([scriptblock]::Create($localBuild[0].Extent.Text)) }
        catch {
            if (!$_.ToString().Contains('Synthetic bootstrap caller failure')) { throw }
            $failed = $true
        }
        if ($failed -ne $case.Throws -or ($reactorCalls -join ',') -cne $case.Calls) {
            throw "Local launcher bootstrap order/failure/NoBuild contract failed: NoBuild=$NoBuild Failure=$failBootstrap Calls=$reactorCalls"
        }
        $passed++
    }
    [IO.File]::WriteAllText((Join-Path $scriptDirectory 'Initialize-ReactorPackages.ps1'),
        "throw 'Synthetic bootstrap caller failure.'")
    foreach ($workflow in 'ci.yml', 'codeql.yml', 'release-candidate-e2e.yml') {
        $text = Get-Content (Join-Path $repo ".github\workflows\$workflow") -Raw
        $blocks = [regex]::Matches($text, '(?m)^(?<indent> +)run: \|\r?\n(?<body>(?:\k<indent>  .*(?:\r?\n|$))+)' )
        foreach ($block in $blocks) {
            $body = $block.Groups['body'].Value
            if ($body -notmatch 'Initialize-ReactorPackages.ps1') { continue }
            if ($body -notmatch '\$ErrorActionPreference\s*=\s*''Stop''') { throw "Nonterminating bootstrap caller: $workflow" }
            $initializer = ($body -split '\r?\n' | Where-Object { $_ -match 'Initialize-ReactorPackages.ps1' } | Select-Object -First 1).Trim()
            $priorLocation = Get-Location
            $previousWorkspace = $env:GITHUB_WORKSPACE
            try {
                Set-Location $directory
                $env:GITHUB_WORKSPACE = $directory
                $script:dotnetReached = $false
                $failed = $false
                $invocation = [scriptblock]::Create("`$ErrorActionPreference='Stop'`n$initializer`n`$script:dotnetReached=`$true")
                try { & $invocation } catch { $failed = $_.ToString().Contains('Synthetic bootstrap caller failure') }
                if (!$failed -or $dotnetReached) { throw "CI continued after bootstrap failure: $workflow" }
            } finally {
                Set-Location $priorLocation
                $env:GITHUB_WORKSPACE = $previousWorkspace
            }
            $passed++
        }
    }
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
Write-Host "Reactor package bootstrap: $passed controls passed."
