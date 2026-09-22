<#
.SYNOPSIS
    Offline, dependency-free tests of durable MSIX allocations and metadata.
.DESCRIPTION
    Replaces Invoke-RestMethod with an in-memory Git database. Races interleave
    independent allocators at the atomic create-ref boundary. No real GitHub
    requests, builds, ref mutations, or certificate operations are performed.
#>
[CmdletBinding()]
param([string]$RepoRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path $PSScriptRoot -Parent }
$library = Join-Path $RepoRoot 'scripts\MsixVersioning.ps1'
$entrypoint = Join-Path $RepoRoot 'scripts\Resolve-MsixPackageVersion.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-msix-version-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$baselinePath = Join-Path $temporaryRoot 'baseline.json'
$metadataPath = Join-Path $temporaryRoot 'version-info.json'
$token = 'test-token'
$commit = 'a' * 40
$otherCommit = 'b' * 40
$repository = 'openclaw/openclaw-windows-node'
$msixTestState = @{
    cases = 0; assertions = 0; totalRequests = 0
    refs = @{}; tags = @{}; sourceRefs = @{}; requests = $null; nextSha = 0
    onCreateRef = $null; onRequest = $null; tamper = $null
    collisionStatus = 422; raceResults = $null; expectAuthorization = $true; token = $token
}
$oldToken = $env:GH_TOKEN

function Assert-Equal {
    param([AllowNull()][object]$Actual, [AllowNull()][object]$Expected)
    $msixTestState.assertions++
    if ($Actual -cne $Expected) { throw "Expected '$Expected', received '$Actual'." }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Expected)
    $msixTestState.assertions++
    $failure = $null
    try { & $Action | Out-Null }
    catch { $failure = $_ }
    if ($null -eq $failure) { throw "Expected failure containing '$Expected', but the operation succeeded." }
    if ($failure.Exception.Message.IndexOf($Expected, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Expected '$Expected', received '$($failure.Exception.Message)'."
    }
    if (($failure | Out-String).Contains($token) -or $failure.Exception.ToString().Contains($token)) {
        throw 'A failure leaked the synthetic API token.'
    }
}

function Reset-Fixture {
    $msixTestState.refs = @{}
    $msixTestState.tags = @{}
    $msixTestState.sourceRefs = @{}
    $msixTestState.requests = [Collections.Generic.List[object]]::new()
    $msixTestState.nextSha = 0
    $msixTestState.onCreateRef = $null
    $msixTestState.onRequest = $null
    $msixTestState.tamper = $null
    $msixTestState.collisionStatus = 422
    $msixTestState.raceResults = [Collections.Generic.List[object]]::new()
    $msixTestState.expectAuthorization = $true
    Set-Content -LiteralPath $baselinePath -Value '{"schemaVersion":1,"lastAllocated":{"2026.9.4":400}}'
}

function Test-Case {
    param([string]$Name, [scriptblock]$Action)
    Reset-Fixture
    try { & $Action | Out-Null }
    catch { throw "Case '$Name' failed: $($_.Exception.Message)" }
    $msixTestState.cases++
}

function New-Arguments {
    param([string]$Version = '2026.9.4-alpha.1', [switch]$Preview, [string]$Commit = $commit)
    if (-not $Preview) {
        $ref = "refs/tags/v$Version"
        $msixTestState.sourceRefs[$ref] = [pscustomobject]@{
            ref = $ref
            object = [pscustomobject]@{ type = 'commit'; sha = $Commit.ToLowerInvariant() }
        }
    }
    @{
        SourceVersion = $Version
        SourceCommit = $Commit
        SourceRef = if ($Preview) { 'refs/pull/1445/merge' } else { "refs/tags/v$Version" }
        Repository = $repository
        Reserve = -not $Preview
        GitHubToken = $token
        BaselinePath = $baselinePath
    }
}

function New-Record {
    param([int]$Counter, [string]$Version = '2026.9.4-alpha.1', [string]$Commit = $commit)
    $base = ($Version -split '[-+]')[0]
    $parts = $base.Split('.')
    $packageBase = '{0}.{1}.{2}' -f $parts[0], $parts[1], $Counter
    [pscustomobject][ordered]@{
        schemaVersion = 1
        sourceVersion = $Version
        sourceCommit = $Commit
        sourceRef = "refs/tags/v$Version"
        repository = $repository
        baseVersion = $base
        packageBaseVersion = $packageBase
        storePackageVersion = "$packageBase.0"
        packagingRevision = $Counter - ([int]$parts[2] * 100)
        allocation = 'reserved'
        reservationRef = "refs/tags/msix-package/$base/$Counter"
    }
}

function Add-TagObject {
    param([string]$Name, [string]$Message, [string]$Commit)
    $msixTestState.nextSha++
    $sha = '{0:x40}' -f $msixTestState.nextSha
    $tag = [pscustomobject]@{
        sha = $sha
        tag = $Name
        message = $Message
        object = [pscustomobject]@{ type = 'commit'; sha = $Commit }
    }
    $msixTestState.tags[$sha] = $tag
    return $tag
}

function Add-Reservation {
    param([object]$Record)
    $tag = Add-TagObject -Name $Record.reservationRef.Substring(10) `
        -Message ($Record | ConvertTo-Json -Compress) -Commit $Record.sourceCommit
    $msixTestState.refs[$Record.reservationRef] = [pscustomobject]@{
        ref = $Record.reservationRef
        object = [pscustomobject]@{ type = 'tag'; sha = $tag.sha }
    }
    return $tag
}

function Set-SourceTagChain {
    param([string]$Ref, [string]$Commit, [int]$Depth)
    $target = [pscustomobject]@{ type = 'commit'; sha = $Commit }
    for ($level = 0; $level -lt $Depth; $level++) {
        $tag = Add-TagObject -Name "release-$level" -Message 'Release notes, not allocation JSON.' -Commit $Commit
        $tag.object = $target
        $target = [pscustomobject]@{ type = 'tag'; sha = $tag.sha }
    }
    $msixTestState.sourceRefs[$Ref] = [pscustomobject]@{ ref = $Ref; object = $target }
}

function Throw-ApiError {
    param([int]$Status = 0)
    $exception = [InvalidOperationException]::new("Synthetic private response includes $token.")
    if ($Status -gt 0) {
        $exception | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode = $Status })
    }
    throw $exception
}

function Invoke-RestMethod {
    [CmdletBinding()]
    param([string]$Method, [string]$Uri, [hashtable]$Headers, [string]$UserAgent,
        [int]$MaximumRedirection, [string]$ContentType, [string]$Body)

    $msixTestState.totalRequests++
    if ($Method -cne 'GET' -and $Method -cne 'POST') { throw 'Destructive or unexpected HTTP method.' }
    $prefix = "https://api.github.com/repos/$repository/git/"
    if (-not $Uri.StartsWith($prefix, [StringComparison]::Ordinal) -or $Uri.Contains('?')) {
        throw 'Unexpected API origin, repository, or undocumented pagination query.'
    }
    if ($Uri.Contains($msixTestState.token) -or ($null -ne $Body -and $Body.Contains($msixTestState.token))) {
        throw 'Token escaped its Authorization header.'
    }
    Assert-Equal $Headers.Accept 'application/vnd.github+json'
    Assert-Equal $Headers['X-GitHub-Api-Version'] '2022-11-28'
    if ($msixTestState.expectAuthorization) {
        Assert-Equal $Headers.Authorization ([string]::Concat('Bearer ', $token))
    }
    else { Assert-Equal $Headers.ContainsKey('Authorization') $false }
    Assert-Equal $UserAgent 'OpenClaw-MsixVersionAllocator'
    Assert-Equal $MaximumRedirection 0
    $path = $Uri.Substring($prefix.Length)
    $data = if ($Body) { $Body | ConvertFrom-Json } else { $null }
    $msixTestState.requests.Add([pscustomobject]@{ method = $Method; path = $path; body = $data })
    if ($null -ne $msixTestState.onRequest) { & $msixTestState.onRequest $Method $path $data }

    $result = $null
    if ($Method -ceq 'GET' -and $path.StartsWith('matching-refs/')) {
        if ($path -cnotmatch '\Amatching-refs/tags/msix-package/[1-9][0-9]*\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)/\z') {
            throw 'Matching-refs was not narrowly scoped to one canonical base.'
        }
        $refPrefix = 'refs/' + $path.Substring('matching-refs/'.Length)
        $result = @($msixTestState.refs.Values | Where-Object { $_.ref.StartsWith($refPrefix, [StringComparison]::Ordinal) })
    }
    elseif ($Method -ceq 'GET' -and $path.StartsWith('ref/tags/')) {
        $ref = 'refs/tags/' + [Uri]::UnescapeDataString($path.Substring('ref/tags/'.Length))
        if (-not $msixTestState.sourceRefs.ContainsKey($ref)) { Throw-ApiError 404 }
        $result = $msixTestState.sourceRefs[$ref]
    }
    elseif ($Method -ceq 'GET' -and $path -cmatch '\Atags/[0-9a-f]{40}\z') {
        $sha = $path.Substring(5)
        if (-not $msixTestState.tags.ContainsKey($sha)) { Throw-ApiError 404 }
        $result = $msixTestState.tags[$sha]
    }
    elseif ($Method -ceq 'POST' -and $path -ceq 'tags') {
        Assert-Equal $ContentType 'application/json; charset=utf-8'
        Assert-Equal $data.type 'commit'
        $result = Add-TagObject -Name $data.tag -Message $data.message -Commit $data.object
    }
    elseif ($Method -ceq 'POST' -and $path -ceq 'refs') {
        if ($null -ne $msixTestState.onCreateRef) { & $msixTestState.onCreateRef $data }
        if ($msixTestState.refs.ContainsKey($data.ref)) { Throw-ApiError $msixTestState.collisionStatus }
        if (-not $msixTestState.tags.ContainsKey($data.sha)) { Throw-ApiError 422 }
        $result = [pscustomobject]@{ ref = $data.ref; object = [pscustomobject]@{ type = 'tag'; sha = $data.sha } }
        $msixTestState.refs[$data.ref] = $result
    }
    else { throw 'Unexpected HTTP route, including a forbidden ref update or delete.' }
    if ($null -ne $msixTestState.tamper) { $result = & $msixTestState.tamper $Method $path $result }
    return ,$result
}

try {
    $env:GH_TOKEN = $token
    Reset-Fixture
    $loaded = @(. $library)
    Assert-Equal $loaded.Count 0
    Assert-Equal $msixTestState.requests.Count 0
    $msixTestState.cases++

    Test-Case 'entrypoint returns one object and bootstraps the default baseline' {
        $arguments = New-Arguments
        $arguments.Remove('BaselinePath')
        $arguments.Remove('GitHubToken')
        $arguments.SourceCommit = $commit.ToUpperInvariant()
        $output = @(& $entrypoint @arguments)
        Assert-Equal $output.Count 1
        Assert-Equal ($output[0] -is [pscustomobject]) $true
        Assert-Equal $output[0].storePackageVersion '2026.9.401.0'
        Assert-Equal $output[0].packagingRevision 1
        Assert-Equal $output[0].sourceCommit $commit
        Assert-Equal $output[0].allocation 'reserved'
        Assert-Equal $msixTestState.refs.Count 1
        Assert-Equal $msixTestState.requests.Count 4
        Assert-Equal $msixTestState.requests[0].path 'ref/tags/v2026.9.4-alpha.1'
        $stored = $msixTestState.tags[$msixTestState.refs[$output[0].reservationRef].object.sha].message | ConvertFrom-Json
        Assert-Equal ($stored | ConvertTo-Json -Compress) ($output[0] | ConvertTo-Json -Compress)
    }
    Test-Case 'first allocation without a baseline entry starts at patch times 100' {
        Set-Content -LiteralPath $baselinePath '{"schemaVersion":1,"lastAllocated":{}}'
        $arguments = New-Arguments
        $result = Resolve-MsixPackageVersion @arguments
        Assert-Equal $result.storePackageVersion '2026.9.400.0'
        Assert-Equal $result.packagingRevision 0
    }
    Test-Case 'alpha stable and multi-digit correction share one counter' {
        $counter = 401
        foreach ($version in @('2026.9.4-alpha.1', '2026.9.4-alpha.10', '2026.9.4', '2026.9.4-10', '2026.9.4-11')) {
            $arguments = New-Arguments $version
            $result = Resolve-MsixPackageVersion @arguments
            Assert-Equal $result.storePackageVersion "2026.9.$counter.0"
            Assert-Equal $result.sourceVersion $version
            $counter++
        }
    }
    foreach ($case in @(
        @{ Version = '2026.9.5-alpha.1'; Expected = '2026.9.500.0' },
        @{ Version = '2026.10.1'; Expected = '2026.10.100.0' },
        @{ Version = '2026.11.10-11'; Expected = '2026.11.1000.0' },
        @{ Version = '2026.11.11-alpha.999'; Expected = '2026.11.1100.0' },
        @{ Version = '65535.65535.655'; Expected = '65535.65535.65500.0' },
        @{ Version = '1.0.0'; Expected = '1.0.0.0' }
    )) {
        Test-Case "fresh range $($case.Version)" {
            $arguments = New-Arguments $case.Version
            Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion $case.Expected
        }
    }
    Test-Case 'greatest ref or baseline determines the next number' {
        Add-Reservation (New-Record 420) | Out-Null
        $arguments = New-Arguments '2026.9.4-alpha.2'
        Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion '2026.9.421.0'
        Set-Content -LiteralPath $baselinePath '{"schemaVersion":1,"lastAllocated":{"2026.9.4":450}}'
        $arguments = New-Arguments '2026.9.4-alpha.3'
        Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion '2026.9.451.0'
    }
    Test-Case 'revision 99 then exhausted with idempotent rerun still available' {
        Add-Reservation (New-Record 498) | Out-Null
        $arguments = New-Arguments '2026.9.4-alpha.2'
        $reserved = Resolve-MsixPackageVersion @arguments
        Assert-Equal $reserved.packagingRevision 99
        Assert-Equal $reserved.storePackageVersion '2026.9.499.0'
        Assert-Equal (Resolve-MsixPackageVersion @arguments).reservationRef $reserved.reservationRef
        Set-Content -LiteralPath $baselinePath '{"schemaVersion":1,"lastAllocated":{"2026.9.4":499}}'
        Assert-Equal (Resolve-MsixPackageVersion @arguments).reservationRef $reserved.reservationRef
        $arguments = New-Arguments '2026.9.4-alpha.3'
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'range exhausted'
        $arguments.Reserve = $false
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'range exhausted'
    }
    Test-Case 'partial UInt16 range stops at 65535 and never wraps' {
        Add-Reservation (New-Record 65534 '2026.9.655-alpha.1') | Out-Null
        $arguments = New-Arguments '2026.9.655-alpha.2'
        $result = Resolve-MsixPackageVersion @arguments
        Assert-Equal $result.storePackageVersion '2026.9.65535.0'
        Assert-Equal $result.packagingRevision 35
        $arguments = New-Arguments '2026.9.655-alpha.3'
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'range exhausted'
    }
    Test-Case 'rerun reuses failed-build reservation without any new POST' {
        $arguments = New-Arguments
        $first = Resolve-MsixPackageVersion @arguments
        $postCount = @($msixTestState.requests | Where-Object method -eq POST).Count
        $second = Resolve-MsixPackageVersion @arguments
        Assert-Equal ($second | ConvertTo-Json -Compress) ($first | ConvertTo-Json -Compress)
        Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count $postCount
    }
    Test-Case 'moved source tag is rejected' {
        Add-Reservation (New-Record 401) | Out-Null
        $arguments = New-Arguments -Commit $otherCommit
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'source tag moved'
        Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
    }
    foreach ($depth in @(1, 2, 8)) {
        Test-Case "official source resolves through $depth annotated tags" {
            $arguments = New-Arguments
            Set-SourceTagChain -Ref $arguments.SourceRef -Commit $commit -Depth $depth
            $result = Resolve-MsixPackageVersion @arguments
            Assert-Equal $result.sourceCommit $commit
            Assert-Equal $result.storePackageVersion '2026.9.401.0'
            Assert-Equal $msixTestState.requests.Count (4 + $depth)
            Assert-Equal @($msixTestState.requests | Where-Object { $_.method -eq 'GET' -and $_.path.StartsWith('tags/') }).Count $depth
        }
    }
    Test-Case 'source lookup URI escapes SemVer metadata without changing provenance' {
        $arguments = New-Arguments '2026.9.4-alpha.1+build.7'
        $result = Resolve-MsixPackageVersion @arguments
        Assert-Equal $result.sourceRef 'refs/tags/v2026.9.4-alpha.1+build.7'
        Assert-Equal $msixTestState.requests[0].path 'ref/tags/v2026.9.4-alpha.1%2Bbuild.7'
    }
    Test-Case 'a source tag must exist even when a reservation already exists' {
        $arguments = New-Arguments
        Add-Reservation (New-Record 401) | Out-Null
        $msixTestState.sourceRefs.Clear()
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'HTTP 404'
        Assert-Equal $msixTestState.requests.Count 1
        Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
    }
    foreach ($depth in @(0, 2)) {
        Test-Case "wrong source commit after $depth tag peels is rejected" {
            $arguments = New-Arguments
            Set-SourceTagChain -Ref $arguments.SourceRef -Commit $otherCommit -Depth $depth
            Assert-Throws { Resolve-MsixPackageVersion @arguments } 'does not resolve to the requested SourceCommit'
            Assert-Equal $msixTestState.requests.Count (1 + $depth)
            Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
        }
    }
    Test-Case 'existing reservation cannot bypass a source tag moved since the original run' {
        $arguments = New-Arguments
        Add-Reservation (New-Record 401) | Out-Null
        $msixTestState.sourceRefs[$arguments.SourceRef].object.sha = $otherCommit
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'does not resolve'
        Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
    }
    Test-Case 'source tag is reverified after a competing reservation' {
        $arguments = New-Arguments
        $msixTestState.onCreateRef = {
            param($data)
            Add-Reservation (New-Record 401 '2026.9.4-alpha.2' $otherCommit) | Out-Null
            $msixTestState.sourceRefs['refs/tags/v2026.9.4-alpha.1'].object.sha = $otherCommit
        }
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'does not resolve'
        Assert-Equal @($msixTestState.requests | Where-Object { $_.path.StartsWith('ref/tags/') }).Count 2
        Assert-Equal @($msixTestState.requests | Where-Object { $_.method -eq 'POST' -and $_.path -eq 'refs' }).Count 1
        Assert-Equal $msixTestState.refs.Count 1
    }
    foreach ($annotated in @($false, $true)) {
        foreach ($status in @(0, 401, 404, 409, 422)) {
            Test-Case "source provenance lookup annotated=$annotated HTTP $status fails closed" {
                $arguments = New-Arguments
                if ($annotated) { Set-SourceTagChain -Ref $arguments.SourceRef -Commit $commit -Depth 1 }
                $msixTestState.onRequest = {
                    param($method, $path, $data)
                    if (($annotated -and $path.StartsWith('tags/')) -or
                        (-not $annotated -and $path.StartsWith('ref/tags/'))) { Throw-ApiError $status }
                }
                Assert-Throws { Resolve-MsixPackageVersion @arguments } 'MSIX GitHub API GET failed'
                Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
                Assert-Equal $msixTestState.refs.Count 0
            }
        }
    }
    foreach ($change in @('ref name', 'array ref name', 'type', 'array type', 'sha', 'array sha', 'uppercase sha', 'missing object')) {
        Test-Case "source ref response rejects $change" {
            $arguments = New-Arguments
            $source = $msixTestState.sourceRefs[$arguments.SourceRef]
            switch ($change) {
                'ref name' { $source.ref = 'refs/tags/v2026.9.4-alpha.2' }
                'array ref name' { $source.ref = @($source.ref) }
                'type' { $source.object.type = 'tree' }
                'array type' { $source.object.type = @('commit') }
                'sha' { $source.object.sha = 'invalid' }
                'array sha' { $source.object.sha = @($commit) }
                'uppercase sha' { $source.object.sha = $commit.ToUpperInvariant() }
                'missing object' { $source.PSObject.Properties.Remove('object') }
            }
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
            Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
        }
    }
    foreach ($change in @('sha', 'array sha', 'type', 'array type', 'target sha', 'array target sha', 'missing object')) {
        Test-Case "source annotated response rejects $change" {
            $arguments = New-Arguments
            Set-SourceTagChain -Ref $arguments.SourceRef -Commit $commit -Depth 1
            $sourceTag = $msixTestState.tags[$msixTestState.sourceRefs[$arguments.SourceRef].object.sha]
            switch ($change) {
                'sha' { $sourceTag.sha = 'f' * 40 }
                'array sha' { $sourceTag.sha = @($sourceTag.sha) }
                'type' { $sourceTag.object.type = 'blob' }
                'array type' { $sourceTag.object.type = @('commit') }
                'target sha' { $sourceTag.object.sha = 'invalid' }
                'array target sha' { $sourceTag.object.sha = @($commit) }
                'missing object' { $sourceTag.PSObject.Properties.Remove('object') }
            }
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
            Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
        }
    }
    Test-Case 'source annotated cycles fail without writes' {
        $arguments = New-Arguments
        Set-SourceTagChain -Ref $arguments.SourceRef -Commit $commit -Depth 1
        $sourceTag = $msixTestState.tags[$msixTestState.sourceRefs[$arguments.SourceRef].object.sha]
        $sourceTag.object = [pscustomobject]@{ type = 'tag'; sha = $sourceTag.sha }
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'cycle'
        Assert-Equal $msixTestState.requests.Count 2
        Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
    }
    Test-Case 'source annotated depth nine exceeds the eight-object bound' {
        $arguments = New-Arguments
        Set-SourceTagChain -Ref $arguments.SourceRef -Commit $commit -Depth 9
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'depth limit'
        Assert-Equal $msixTestState.requests.Count 9
        Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
    }
    foreach ($same in @($false, $true)) {
        foreach ($status in @(409, 422)) {
            Test-Case "concurrent same=$same source with HTTP $status collision" {
                $msixTestState.collisionStatus = $status
                $msixTestState.onCreateRef = {
                    param($data)
                    $msixTestState.onCreateRef = $null
                    $competitor = if ($same) { New-Arguments } else { New-Arguments '2026.9.4-alpha.2' -Commit $otherCommit }
                    $msixTestState.raceResults.Add((Resolve-MsixPackageVersion @competitor))
                }
                $arguments = New-Arguments
                $result = Resolve-MsixPackageVersion @arguments
                Assert-Equal $msixTestState.raceResults.Count 1
                Assert-Equal $msixTestState.raceResults[0].storePackageVersion '2026.9.401.0'
                if ($same) {
                    Assert-Equal $result.reservationRef $msixTestState.raceResults[0].reservationRef
                    Assert-Equal $msixTestState.refs.Count 1
                }
                else {
                    Assert-Equal $result.storePackageVersion '2026.9.402.0'
                    Assert-Equal $msixTestState.refs.Count 2
                }
            }
        }
    }
    Test-Case 'bounded retry exhaustion leaves all competing reservations intact' {
        $msixTestState.onCreateRef = {
            param($data)
            $number = [int]($data.ref.Split('/')[-1])
            Add-Reservation (New-Record $number "2026.9.4-alpha.$number" $otherCommit) | Out-Null
        }
        $arguments = New-Arguments
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'retry limit exhausted'
        Assert-Equal $msixTestState.refs.Count 8
        Assert-Equal @($msixTestState.requests | Where-Object { $_.method -eq 'POST' -and $_.path -eq 'refs' }).Count 8
        Assert-Equal @($msixTestState.requests | Where-Object { $_.path.StartsWith('matching-refs/') }).Count 9
    }
    Test-Case 'last allowed collision still converges on duplicate source' {
        $msixTestState.onCreateRef = {
            param($data)
            $number = [int]($data.ref.Split('/')[-1])
            $record = if ($number -eq 408) { New-Record $number } else { New-Record $number "2026.9.4-alpha.$number" $otherCommit }
            Add-Reservation $record | Out-Null
        }
        $arguments = New-Arguments
        Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion '2026.9.408.0'
        Assert-Equal $msixTestState.refs.Count 8
    }
    Test-Case 'a lost response after persisted reservation is recovered by rerun' {
        $msixTestState.tamper = {
            param($method, $path, $result)
            if ($method -eq 'POST' -and $path -eq 'refs') { Throw-ApiError }
            return ,$result
        }
        $arguments = New-Arguments
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'network or transport'
        Assert-Equal $msixTestState.refs.Count 1
        $msixTestState.tamper = $null
        Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion '2026.9.401.0'
    }
    Test-Case 'preview reads only newest record and never creates anything' {
        $oldTag = Add-Reservation (New-Record 401)
        $oldTag.message = 'not read by preview'
        Add-Reservation (New-Record 415 '2026.9.4-alpha.2') | Out-Null
        $arguments = New-Arguments '2026.9.4-PullRequest1445.11+Branch.feature.Sha.abcdef' -Preview
        $result = Resolve-MsixPackageVersion @arguments
        Assert-Equal $result.storePackageVersion '2026.9.416.0'
        Assert-Equal $result.allocation 'preview'
        Assert-Equal $result.reservationRef $null
        Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
        Assert-Equal @($msixTestState.requests | Where-Object { $_.path.StartsWith('tags/') }).Count 1
        Add-Reservation (New-Record 416 '2026.9.4-alpha.3') | Out-Null
        Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion '2026.9.417.0'
        $arguments = New-Arguments $arguments.SourceVersion
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'not valid allocation JSON'
    }
    Test-Case 'preview normalizes a single matching ref unwrapped by Windows PowerShell' {
        Add-Reservation (New-Record 401) | Out-Null
        $msixTestState.tamper = {
            param($method, $path, $result)
            if ($method -eq 'GET' -and $path.StartsWith('matching-refs/')) {
                return $result[0]
            }
            return ,$result
        }
        $arguments = New-Arguments -Preview
        Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion '2026.9.402.0'
    }
    foreach ($sourceRef in @('refs/heads/main', 'refs/tags/v2026.9.4-alpha.1', 'refs/pull/1445/merge')) {
        Test-Case "preview accepts $sourceRef" {
            $arguments = New-Arguments -Preview
            $arguments.SourceRef = $sourceRef
            $result = Resolve-MsixPackageVersion @arguments
            Assert-Equal $result.storePackageVersion '2026.9.401.0'
            Assert-Equal $result.sourceRef $sourceRef
            Assert-Equal $msixTestState.requests.Count 1
            Assert-Equal $msixTestState.refs.Count 0
        }
    }
    Test-Case 'anonymous preview is read-only and omits Authorization' {
        $msixTestState.expectAuthorization = $false
        $arguments = New-Arguments -Preview
        $arguments.GitHubToken = ''
        Assert-Equal (& $entrypoint @arguments).allocation 'preview'
        Assert-Equal $msixTestState.requests.Count 1
        Assert-Equal $msixTestState.refs.Count 0
    }
    foreach ($version in @('v2026.9.4', '2026.09.4', '02026.9.4', '2026.9.04', '2026.9', '2026.9.4.1',
        '2026.9.4-', '2026.9.4-alpha..1', '2026.9.4-alpha.01', '2026.9.4-01', '2026.9.4+',
        '2026.9.4+bad..meta', '2026.9.4-alpha_1', "2026.9.4`n", '0.9.4', '65536.9.4',
        '2026.65536.4', '2026.9.656', '2026.9.999999999999999999999', '2026.9.4;Write-Host hacked')) {
        Test-Case "reject invalid source version $version" {
            $arguments = New-Arguments $version
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
            Assert-Equal $msixTestState.requests.Count 0
        }
    }
    foreach ($mutation in @(
        @{ Field = 'SourceCommit'; Value = 'a' * 39 },
        @{ Field = 'SourceCommit'; Value = 'g' * 40 },
        @{ Field = 'SourceRef'; Value = 'refs/heads/main' },
        @{ Field = 'SourceRef'; Value = 'refs/tags/v2026.9.4-alpha.2' },
        @{ Field = 'SourceRef'; Value = "refs/tags/v2026.9.4-alpha.1`n" },
        @{ Field = 'Repository'; Value = 'openclaw/../evil' },
        @{ Field = 'Repository'; Value = 'https://example.com/repo' },
        @{ Field = 'Repository'; Value = 'openclaw/repo?access_token=x' },
        @{ Field = 'Repository'; Value = 'openclaw/..' },
        @{ Field = 'GitHubToken'; Value = '' },
        @{ Field = 'GitHubToken'; Value = "token`r`nHeader: injected" }
    )) {
        Test-Case "reject invalid input $($mutation.Field)" {
            $arguments = New-Arguments
            $arguments[$mutation.Field] = $mutation.Value
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
            Assert-Equal $msixTestState.requests.Count 0
        }
    }
    foreach ($baseline in @(
        'not JSON', 'null', '[]', '[{"schemaVersion":1,"lastAllocated":{}}]', '{"schemaVersion":2,"lastAllocated":{}}',
        '{"schemaVersion":"1","lastAllocated":{}}', '{"schemaVersion":1}',
        '{"schemaVersion":1,"lastAllocated":[]}', '{"schemaVersion":1,"lastAllocated":{"2026.09.4":400}}',
        '{"schemaVersion":1,"lastAllocated":{"2026.9.4-alpha.1":400}}',
        '{"schemaVersion":1,"lastAllocated":{"2026.9.4":399}}',
        '{"schemaVersion":1,"lastAllocated":{"2026.9.4":500}}',
        '{"schemaVersion":1,"lastAllocated":{"2026.9.4":"400"}}',
        '{"schemaVersion":1,"lastAllocated":{"2026.9.4":400.5}}',
        '{"schemaVersion":1,"lastAllocated":{"2026.9.655":65536}}',
        '{"schemaVersion":1,"lastAllocated":{"2026.9.656":65600}}'
    )) {
        Test-Case 'reject corrupt baseline including unrelated base records' {
            Set-Content -LiteralPath $baselinePath $baseline
            $arguments = New-Arguments
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
            Assert-Equal $msixTestState.requests.Count 0
        }
    }
    Test-Case 'missing baseline is an error' {
        $arguments = New-Arguments
        $arguments.BaselinePath = Join-Path $temporaryRoot 'absent.json'
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'baseline file'
        Assert-Equal $msixTestState.requests.Count 0
    }
    foreach ($status in @(0, 401, 404, 409, 422)) {
        Test-Case "matching-refs error $status is never a zero fallback" {
            $msixTestState.onRequest = { param($method, $path, $data) Throw-ApiError $status }
            $arguments = New-Arguments -Preview
            Assert-Throws { Resolve-MsixPackageVersion @arguments } 'MSIX GitHub API GET failed'
            Assert-Equal $msixTestState.requests.Count 1
        }
    }
    foreach ($route in @('tags', 'refs')) {
        foreach ($status in @(0, 401, 404, 409, 422)) {
            Test-Case "$route HTTP $status does not silently succeed" {
                $msixTestState.onRequest = {
                    param($method, $path, $data)
                    if ($method -eq 'POST' -and $path -eq $route) { Throw-ApiError $status }
                }
                $arguments = New-Arguments
                $expected = if ($route -eq 'refs' -and $status -in @(409, 422)) { 'no competing reservation' } else { 'MSIX GitHub API POST failed' }
                Assert-Throws { Resolve-MsixPackageVersion @arguments } $expected
                Assert-Equal $msixTestState.refs.Count 0
                Assert-Equal @($msixTestState.requests | Where-Object { $_.method -eq 'POST' -and $_.path -eq $route }).Count 1
            }
        }
    }
    foreach ($status in @(0, 401, 404, 422)) {
        Test-Case "annotated tag GET error $status fails closed" {
            Add-Reservation (New-Record 401) | Out-Null
            $msixTestState.onRequest = {
                param($method, $path, $data)
                if ($path.StartsWith('tags/')) { Throw-ApiError $status }
            }
            $arguments = New-Arguments
            Assert-Throws { Resolve-MsixPackageVersion @arguments } 'MSIX GitHub API GET failed'
        }
    }
    foreach ($badRef in @('refs/tags/msix-package/2026.9.5/500', 'refs/tags/msix-package/2026.9.4/0401',
        'refs/tags/msix-package/2026.9.4/399', 'refs/tags/msix-package/2026.9.4/500',
        'refs/tags/msix-package/2026.9.4/401/extra', 'refs/tags/msix-package/2026.9.4/999999999999999',
        'refs/tags/msix-package/2026.9.4/401x')) {
        Test-Case "reject malformed or unrelated matching ref $badRef" {
            Add-Reservation (New-Record 401) | Out-Null
            $msixTestState.tamper = {
                param($method, $path, $result)
                if ($path.StartsWith('matching-refs/')) { $result[0].ref = $badRef }
                return ,$result
            }
            $arguments = New-Arguments -Preview
            Assert-Throws { Resolve-MsixPackageVersion @arguments } 'reservation ref'
        }
    }
    foreach ($change in @('lightweight', 'array type', 'bad sha', 'duplicate', 'too many', 'missing field')) {
        Test-Case "reject matching-refs shape: $change" {
            Add-Reservation (New-Record 401) | Out-Null
            $msixTestState.tamper = {
                param($method, $path, $result)
                if ($path.StartsWith('matching-refs/')) {
                    switch ($change) {
                        'lightweight' { $result[0].object.type = 'commit' }
                        'array type' { $result[0].object.type = @('tag') }
                        'bad sha' { $result[0].object.sha = 'bad' }
                        'duplicate' { $result = @($result[0], $result[0]) }
                        'too many' { $result = @($result[0]) * 101 }
                        'missing field' { $result[0].PSObject.Properties.Remove('object') }
                    }
                }
                return ,$result
            }
            $arguments = New-Arguments -Preview
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
        }
    }
    foreach ($change in @('sha', 'name', 'array name', 'type', 'array type', 'target', 'json', 'array message', 'message type', 'record commit',
        'record ref', 'record version', 'record repository', 'record preview', 'record sourceRef')) {
        Test-Case "reject corrupt annotated reservation: $change" {
            $tag = Add-Reservation (New-Record 401)
            $record = $tag.message | ConvertFrom-Json
            switch ($change) {
                'sha' { $tag.sha = 'f' * 40 }
                'name' { $tag.tag = 'msix-package/2026.9.4/402' }
                'array name' { $tag.tag = @($tag.tag) }
                'type' { $tag.object.type = 'tag' }
                'array type' { $tag.object.type = @('commit') }
                'target' { $tag.object.sha = $otherCommit }
                'json' { $tag.message = 'not JSON' }
                'array message' { $tag.message = '[' + $tag.message + ']' }
                'message type' { $tag.message = 1 }
                'record commit' { $record.sourceCommit = $otherCommit }
                'record ref' { $record.reservationRef = 'refs/tags/msix-package/2026.9.4/402' }
                'record version' { $record.sourceVersion = '2026.9.5-alpha.1' }
                'record repository' { $record.repository = 'other/repo' }
                'record preview' { $record.allocation = 'preview'; $record.reservationRef = $null }
                'record sourceRef' { $record.sourceRef = 'refs/tags/v2026.9.4-alpha.2' }
            }
            if ($change.StartsWith('record ')) { $tag.message = $record | ConvertTo-Json -Compress }
            $arguments = New-Arguments -Preview
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
            Assert-Equal @($msixTestState.requests | Where-Object method -eq POST).Count 0
        }
    }
    Test-Case 'duplicate stored allocations for one source are corrupt' {
        Add-Reservation (New-Record 401) | Out-Null
        Add-Reservation (New-Record 402) | Out-Null
        $arguments = New-Arguments
        Assert-Throws { Resolve-MsixPackageVersion @arguments } 'duplicate sourceRef'
    }
    Test-Case 'official inspection is bounded to 100 annotated records' {
        foreach ($number in 400..499) {
            Add-Reservation (New-Record $number "2026.9.4-alpha.$number") | Out-Null
        }
        $arguments = New-Arguments '2026.9.4-alpha.400'
        Assert-Equal (Resolve-MsixPackageVersion @arguments).storePackageVersion '2026.9.400.0'
        Assert-Equal @($msixTestState.requests | Where-Object { $_.path.StartsWith('tags/') }).Count 100
        Assert-Equal $msixTestState.requests.Count 102
    }
    foreach ($change in @('tag SHA', 'tag target', 'tag message', 'ref name', 'array ref name', 'ref type', 'array ref type', 'ref SHA', 'array ref SHA')) {
        Test-Case "reject POST response tampering: $change" {
            $msixTestState.tamper = {
                param($method, $path, $result)
                if ($method -eq 'POST' -and $path -eq 'tags') {
                    switch ($change) {
                        'tag SHA' { $result.sha = 'bad' }
                        'tag target' { $result.object.sha = $otherCommit }
                        'tag message' {
                            $record = $result.message | ConvertFrom-Json
                            $record.sourceVersion = '2026.9.4-alpha.2'
                            $record.sourceRef = 'refs/tags/v2026.9.4-alpha.2'
                            $result.message = $record | ConvertTo-Json -Compress
                        }
                    }
                }
                if ($method -eq 'POST' -and $path -eq 'refs') {
                    switch ($change) {
                        'ref name' { $result.ref = 'refs/tags/msix-package/2026.9.4/402' }
                        'array ref name' { $result.ref = @($result.ref) }
                        'ref type' { $result.object.type = 'commit' }
                        'array ref type' { $result.object.type = @('tag') }
                        'ref SHA' { $result.object.sha = 'f' * 40 }
                        'array ref SHA' { $result.object.sha = @($result.object.sha) }
                    }
                }
                return ,$result
            }
            $arguments = New-Arguments
            Assert-Throws { Resolve-MsixPackageVersion @arguments } ''
        }
    }
    Test-Case 'metadata validation and JSON reader bind exact source and reservation' {
        $info = New-Record 401
        $info | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
        $result = Read-MsixVersionInfo -Path $metadataPath -SourceCommit $commit.ToUpperInvariant() -SourceVersion $info.sourceVersion -RequireReserved
        Assert-Equal ($result | ConvertTo-Json -Compress) ($info | ConvertTo-Json -Compress)
        Assert-Throws { Read-MsixVersionInfo -Path $metadataPath -SourceCommit $otherCommit } 'sourceCommit'
        Assert-Throws { Read-MsixVersionInfo -Path $metadataPath -SourceCommit $commit -SourceVersion '2026.9.4-alpha.2' } 'sourceVersion'
        $info.allocation = 'preview'
        $info.reservationRef = $null
        $info.sourceRef = 'refs/heads/main'
        Assert-Equal (Assert-MsixVersionInfo -VersionInfo $info -SourceCommit $commit).allocation 'preview'
        Assert-Throws { Assert-MsixVersionInfo -VersionInfo $info -SourceCommit $commit -RequireReserved } 'reserved'
        $info | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
        Assert-Throws { Read-MsixVersionInfo -Path $metadataPath -SourceCommit $commit -RequireReserved } 'reserved'
        Set-Content -LiteralPath $metadataPath ('[' + ($info | ConvertTo-Json -Compress) + ']')
        Assert-Throws { Read-MsixVersionInfo -Path $metadataPath -SourceCommit $commit } 'could not be read'
        Set-Content -LiteralPath $metadataPath 'invalid JSON'
        Assert-Throws { Read-MsixVersionInfo -Path $metadataPath -SourceCommit $commit } 'could not be read'
        Assert-Throws { Read-MsixVersionInfo -Path (Join-Path $temporaryRoot 'absent.json') -SourceCommit $commit } 'could not be read'
        foreach ($name in @('Assert-MsixVersionInfo', 'Read-MsixVersionInfo')) {
            $attributes = (Get-Command $name).Parameters.SourceCommit.Attributes
            $mandatory = @($attributes | Where-Object { $_ -is [Management.Automation.ParameterAttribute] -and $_.Mandatory })
            Assert-Equal $mandatory.Count 1
        }
    }
    foreach ($field in (New-Record 401).PSObject.Properties.Name) {
        Test-Case "metadata rejects missing $field" {
            $info = New-Record 401
            $info.PSObject.Properties.Remove($field)
            Assert-Throws { Assert-MsixVersionInfo -VersionInfo $info -SourceCommit $commit } 'missing field'
        }
    }
    foreach ($mutation in @(
        @{ Field = 'schemaVersion'; Value = 2 }, @{ Field = 'schemaVersion'; Value = '1' },
        @{ Field = 'schemaVersion'; Value = $true }, @{ Field = 'schemaVersion'; Value = 1.0 },
        @{ Field = 'sourceVersion'; Value = 2026 }, @{ Field = 'sourceVersion'; Value = '2026.9.5-alpha.1' },
        @{ Field = 'sourceCommit'; Value = $commit.ToUpperInvariant() },
        @{ Field = 'sourceCommit'; Value = $otherCommit },
        @{ Field = 'sourceCommit'; Value = @($commit) },
        @{ Field = 'sourceRef'; Value = 'refs/heads/main' },
        @{ Field = 'sourceRef'; Value = "refs/tags/v2026.9.4-alpha.1`n" },
        @{ Field = 'repository'; Value = 'https://example.org/repo' },
        @{ Field = 'baseVersion'; Value = '2026.09.4' },
        @{ Field = 'packageBaseVersion'; Value = '2026.9.0401' },
        @{ Field = 'packageBaseVersion'; Value = '2026.9.402' },
        @{ Field = 'storePackageVersion'; Value = '2026.9.401.1' },
        @{ Field = 'storePackageVersion'; Value = '2026.9.65536.0' },
        @{ Field = 'packagingRevision'; Value = -1 }, @{ Field = 'packagingRevision'; Value = 100 },
        @{ Field = 'packagingRevision'; Value = 1.5 }, @{ Field = 'packagingRevision'; Value = '1' },
        @{ Field = 'packagingRevision'; Value = $true },
        @{ Field = 'allocation'; Value = 'Reserved' }, @{ Field = 'allocation'; Value = 'other' },
        @{ Field = 'reservationRef'; Value = $null },
        @{ Field = 'reservationRef'; Value = 'refs/tags/v2026.9.401.0' },
        @{ Field = 'reservationRef'; Value = 'refs/tags/msix-package/2026.9.4/402' }
    )) {
        Test-Case "metadata rejects wrong $($mutation.Field)" {
            $info = New-Record 401
            $info.($mutation.Field) = $mutation.Value
            Assert-Throws { Assert-MsixVersionInfo -VersionInfo $info -SourceCommit $commit } ''
        }
    }
    Test-Case 'preview reservation must be null, not empty' {
        $info = New-Record 401
        $info.allocation = 'preview'
        $info.reservationRef = ''
        Assert-Throws { Assert-MsixVersionInfo -VersionInfo $info -SourceCommit $commit } 'must be null'
    }
    Test-Case 'metadata cannot exceed the partial patch 655 range' {
        $info = New-Record 65536 '2026.9.655'
        Assert-Throws { Assert-MsixVersionInfo -VersionInfo $info -SourceCommit $commit } 'allocation range'
    }
    Test-Case 'success and verbose output never contain the HTTP token' {
        $arguments = New-Arguments
        $output = Resolve-MsixPackageVersion @arguments -Verbose *>&1 | Out-String
        Assert-Equal $output.Contains($token) $false
    }
    Write-Host "MSIX versioning tests passed: $($msixTestState.cases) cases, $($msixTestState.assertions) assertions, $($msixTestState.totalRequests) mocked HTTP requests. No real network or destructive ref operations."
}
finally {
    $env:GH_TOKEN = $oldToken
    [IO.Directory]::Delete($temporaryRoot, $true)
}
