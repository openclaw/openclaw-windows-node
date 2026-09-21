function Get-MsixAppVersion {
    param([Parameter(Mandatory)][string]$SourceVersion)

    $pattern = '\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z'
    $match = [regex]::Match($SourceVersion, $pattern)
    if (-not $match.Success) { throw 'SourceVersion must be a canonical SemVer without a v prefix.' }
    foreach ($identifier in $match.Groups['pre'].Value.Split('.')) {
        if ($identifier -match '\A0[0-9]+\z') {
            throw 'SourceVersion must not contain leading-zero numeric prerelease identifiers.'
        }
    }
    $parts = @{}
    foreach ($name in @('major', 'minor', 'patch')) {
        $number = 0
        if (-not [int]::TryParse($match.Groups[$name].Value, [ref]$number) -or $number -gt 65535) {
            throw 'SourceVersion exceeds the MSIX UInt16 component limit.'
        }
        $parts[$name] = $number
    }
    if ($parts.major -eq 0) { throw 'MSIX requires a nonzero major version.' }
    if ($parts.patch -gt 655) { throw 'The app patch cannot fit its MSIX allocation range within UInt16.' }
    [pscustomobject]@{
        major = $parts.major
        minor = $parts.minor
        patch = $parts.patch
        baseVersion = '{0}.{1}.{2}' -f $parts.major, $parts.minor, $parts.patch
        firstCounter = $parts.patch * 100
        lastCounter = [Math]::Min(65535, $parts.patch * 100 + 99)
    }
}

function Assert-MsixProvenance {
    param([string]$SourceCommit, [string]$SourceRef, [string]$Repository)

    if ($SourceCommit -cnotmatch '\A[0-9a-fA-F]{40}\z') { throw 'SourceCommit must be a 40-hex commit SHA.' }
    if ([string]::IsNullOrWhiteSpace($SourceRef) -or $SourceRef -match '\p{Cc}' -or
        $SourceRef -cnotmatch '\Arefs/(?:(?:heads|tags)/.+|pull/[1-9][0-9]*/(?:merge|head))\z') {
        throw 'SourceRef must be a nonempty branch, tag, or pull-request ref without control characters.'
    }
    if ($Repository -cnotmatch '\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}\z' -or
        $Repository.EndsWith('/.') -or $Repository.EndsWith('/..')) {
        throw 'Repository must be a valid GitHub owner/repo name.'
    }
}

function Get-MsixRequiredProperty {
    param([AllowNull()][object]$Value, [string]$Name)

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw 'Expected a JSON object in MSIX allocation data.'
    }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property -or $property.Name -cne $Name) {
        throw "MSIX allocation data is missing field '$Name'."
    }
    return ,$property.Value
}

function Test-MsixInteger {
    param([AllowNull()][object]$Value)
    return ($Value -is [int] -or $Value -is [long])
}

function ConvertFrom-MsixJsonObject {
    param([AllowNull()][string]$Json)

    if ([string]::IsNullOrWhiteSpace($Json) -or -not $Json.TrimStart().StartsWith('{')) {
        throw 'MSIX allocation JSON must contain one object.'
    }
    try { $value = ConvertFrom-Json -InputObject $Json -ErrorAction Stop }
    catch { throw 'MSIX allocation JSON is malformed.' }
    if ($value -isnot [pscustomobject]) { throw 'MSIX allocation JSON must contain one object.' }
    return $value
}

function Assert-MsixVersionInfo {
    <#
    .SYNOPSIS
        Validates allocation metadata and binds it to the expected source commit.
    .DESCRIPTION
        Returns a validated projection, not an authentication of offline metadata.
        RequireReserved rejects previews. Preview versions are not reservations
        and may advance between reruns when official builds reserve more numbers.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$VersionInfo,
        [Parameter(Mandatory)][string]$SourceCommit,
        [string]$SourceVersion,
        [switch]$RequireReserved
    )

    $values = @{}
    foreach ($name in @('schemaVersion', 'sourceVersion', 'sourceCommit', 'sourceRef', 'repository',
        'baseVersion', 'packageBaseVersion', 'storePackageVersion', 'packagingRevision', 'allocation', 'reservationRef')) {
        $values[$name] = Get-MsixRequiredProperty $VersionInfo $name
    }
    if (-not (Test-MsixInteger $values.schemaVersion) -or $values.schemaVersion -ne 1) {
        throw 'Unsupported MSIX allocation schemaVersion.'
    }
    foreach ($name in @('sourceVersion', 'sourceCommit', 'sourceRef', 'repository',
        'baseVersion', 'packageBaseVersion', 'storePackageVersion', 'allocation')) {
        if ($values[$name] -isnot [string] -or [string]::IsNullOrWhiteSpace($values[$name])) {
            throw "MSIX allocation field '$name' must be a nonempty string."
        }
    }
    Assert-MsixProvenance $values.sourceCommit $values.sourceRef $values.repository
    if ($SourceCommit -cnotmatch '\A[0-9a-fA-F]{40}\z' -or
        $values.sourceCommit -cne $SourceCommit.ToLowerInvariant()) {
        throw 'MSIX allocation sourceCommit does not match the expected lowercase source commit.'
    }
    if ($PSBoundParameters.ContainsKey('SourceVersion') -and $values.sourceVersion -cne $SourceVersion) {
        throw 'MSIX allocation sourceVersion does not match the expected source version.'
    }
    $app = Get-MsixAppVersion $values.sourceVersion
    if (-not (Test-MsixInteger $values.packagingRevision) -or $values.packagingRevision -lt 0 -or
        $values.packagingRevision -gt ($app.lastCounter - $app.firstCounter)) {
        throw 'MSIX packagingRevision is outside the app patch allocation range.'
    }
    $counter = $app.firstCounter + $values.packagingRevision
    $packageBase = '{0}.{1}.{2}' -f $app.major, $app.minor, $counter
    if ($values.baseVersion -cne $app.baseVersion -or $values.packageBaseVersion -cne $packageBase -or
        $values.storePackageVersion -cne "$packageBase.0") {
        throw 'MSIX version arithmetic does not match the source version and packagingRevision.'
    }
    if ($values.allocation -ceq 'reserved') {
        $expectedRef = "refs/tags/msix-package/$($app.baseVersion)/$counter"
        if ($values.reservationRef -isnot [string] -or $values.reservationRef -cne $expectedRef) {
            throw 'MSIX reserved metadata has an invalid reservationRef.'
        }
        if ($values.sourceRef -cne "refs/tags/v$($values.sourceVersion)") {
            throw 'MSIX reserved sourceRef must exactly match refs/tags/v plus sourceVersion.'
        }
    }
    elseif ($values.allocation -ceq 'preview') {
        if ($RequireReserved) { throw 'A reserved MSIX allocation is required; preview metadata is not allowed.' }
        if ($null -ne $values.reservationRef) { throw 'MSIX preview reservationRef must be null.' }
    }
    else { throw 'MSIX allocation must be preview or reserved.' }

    [pscustomobject][ordered]@{
        schemaVersion = 1
        sourceVersion = $values.sourceVersion
        sourceCommit = $values.sourceCommit
        sourceRef = $values.sourceRef
        repository = $values.repository
        baseVersion = $app.baseVersion
        packageBaseVersion = $packageBase
        storePackageVersion = "$packageBase.0"
        packagingRevision = [int]$values.packagingRevision
        allocation = $values.allocation
        reservationRef = $values.reservationRef
    }
}

function Read-MsixVersionInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$SourceCommit,
        [string]$SourceVersion,
        [switch]$RequireReserved
    )

    try { $info = ConvertFrom-MsixJsonObject (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop) }
    catch { throw 'MSIX version info could not be read as JSON.' }
    $arguments = @{ VersionInfo = $info; SourceCommit = $SourceCommit; RequireReserved = $RequireReserved }
    if ($PSBoundParameters.ContainsKey('SourceVersion')) { $arguments.SourceVersion = $SourceVersion }
    Assert-MsixVersionInfo @arguments
}

function Read-MsixBaseline {
    param([string]$Path, [object]$App)

    try { $baseline = ConvertFrom-MsixJsonObject (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop) }
    catch { throw 'MSIX baseline file is missing, unreadable, or malformed JSON.' }
    $schema = Get-MsixRequiredProperty $baseline 'schemaVersion'
    if (-not (Test-MsixInteger $schema) -or $schema -ne 1) { throw 'Unsupported MSIX baseline schemaVersion.' }
    $records = Get-MsixRequiredProperty $baseline 'lastAllocated'
    if ($records -isnot [pscustomobject]) { throw 'MSIX baseline lastAllocated must be an object.' }
    $last = $App.firstCounter - 1
    foreach ($property in $records.PSObject.Properties) {
        $recordApp = Get-MsixAppVersion $property.Name
        if ($property.Name -cne $recordApp.baseVersion -or -not (Test-MsixInteger $property.Value) -or
            $property.Value -lt $recordApp.firstCounter -or $property.Value -gt $recordApp.lastCounter) {
            throw 'MSIX baseline contains an invalid base key or lastAllocated range.'
        }
        if ($property.Name -ceq $App.baseVersion) { $last = [int]$property.Value }
    }
    return $last
}

function Invoke-MsixGitHubApi {
    param(
        [ValidateSet('GET', 'POST')][string]$Method,
        [string]$Repository,
        [string]$Path,
        [AllowEmptyString()][string]$Token,
        [object]$Body
    )

    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    if (-not [string]::IsNullOrEmpty($Token)) {
        $headers.Authorization = [string]::Concat('Bearer ', $Token)
    }
    $arguments = @{
        Method = $Method
        Uri = "https://api.github.com/repos/$Repository/git/$Path"
        Headers = $headers
        UserAgent = 'OpenClaw-MsixVersionAllocator'
        MaximumRedirection = 0
        ErrorAction = 'Stop'
        Verbose = $false
        Debug = $false
    }
    if ($Method -eq 'POST') {
        $arguments.ContentType = 'application/json; charset=utf-8'
        $arguments.Body = $Body | ConvertTo-Json -Depth 10 -Compress
    }
    try {
        $response = Invoke-RestMethod @arguments
        return $response
    }
    catch {
        # Do not propagate response bodies, request headers, or token-bearing inner exceptions.
        $status = 0
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
            $statusProperty = $responseProperty.Value.PSObject.Properties['StatusCode']
            if ($null -ne $statusProperty) { $status = [int]$statusProperty.Value }
        }
        $message = if ($status -gt 0) { "MSIX GitHub API $Method failed (HTTP $status)." } else { "MSIX GitHub API $Method failed (network or transport error)." }
        $failure = [InvalidOperationException]::new($message)
        $failure.Data['MsixHttpStatus'] = $status
        throw $failure
    }
}

function Assert-MsixSourceTag {
    param([string]$Repository, [string]$SourceRef, [string]$SourceCommit, [string]$Token)

    $name = [Uri]::EscapeDataString($SourceRef.Substring('refs/tags/'.Length))
    $ref = Invoke-MsixGitHubApi -Method GET -Repository $Repository -Path "ref/tags/$name" -Token $Token
    $returnedRef = Get-MsixRequiredProperty $ref 'ref'
    if ($returnedRef -isnot [string] -or $returnedRef -cne $SourceRef) {
        throw 'MSIX source tag lookup did not return the exact requested sourceRef.'
    }
    $target = Get-MsixRequiredProperty $ref 'object'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($depth = 0; $depth -le 8; $depth++) {
        $type = Get-MsixRequiredProperty $target 'type'
        $sha = Get-MsixRequiredProperty $target 'sha'
        if ($type -isnot [string] -or $sha -isnot [string] -or $sha -cnotmatch '\A[0-9a-f]{40}\z') {
            throw 'MSIX source tag has an invalid object type or SHA.'
        }
        if ($type -ceq 'commit') {
            if ($sha -cne $SourceCommit) { throw 'MSIX source tag does not resolve to the requested SourceCommit.' }
            return
        }
        if ($type -cne 'tag') { throw 'MSIX source tag must resolve to a commit, not a tree or blob.' }
        if (-not $seen.Add($sha)) { throw 'MSIX source tag contains a cycle in its annotated tag chain.' }
        if ($depth -eq 8) { throw 'MSIX source tag exceeds the annotated tag depth limit of eight.' }
        $tag = Invoke-MsixGitHubApi -Method GET -Repository $Repository -Path "tags/$sha" -Token $Token
        $returnedSha = Get-MsixRequiredProperty $tag 'sha'
        if ($returnedSha -isnot [string] -or $returnedSha -cne $sha) {
            throw 'MSIX source annotated tag response does not match the requested SHA.'
        }
        $target = Get-MsixRequiredProperty $tag 'object'
    }
}

function Get-MsixReservationRefs {
    param([string]$Repository, [object]$App, [string]$Token)

    $prefix = "refs/tags/msix-package/$($App.baseVersion)/"
    $response = @(Invoke-MsixGitHubApi -Method GET -Repository $Repository -Path "matching-refs/tags/msix-package/$($App.baseVersion)/" -Token $Token)
    if ($response.Count -gt 100) {
        throw 'MSIX matching-refs must return an array of at most 100 reservations.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $response) {
        $ref = Get-MsixRequiredProperty $item 'ref'
        $target = Get-MsixRequiredProperty $item 'object'
        $type = Get-MsixRequiredProperty $target 'type'
        $sha = Get-MsixRequiredProperty $target 'sha'
        if ($ref -isnot [string] -or -not $ref.StartsWith($prefix, [StringComparison]::Ordinal) -or
            $ref.Substring($prefix.Length) -cnotmatch '\A(?:0|[1-9][0-9]*)\z') {
            throw 'MSIX matching-refs returned an unrelated or malformed reservation ref.'
        }
        $counter = 0
        if (-not [int]::TryParse($ref.Substring($prefix.Length), [ref]$counter) -or
            $counter -lt $App.firstCounter -or $counter -gt $App.lastCounter -or -not $seen.Add($ref)) {
            throw 'MSIX matching-refs returned an out-of-range or duplicate reservation ref.'
        }
        if ($type -isnot [string] -or $type -cne 'tag' -or $sha -isnot [string] -or $sha -cnotmatch '\A[0-9a-f]{40}\z') {
            throw 'MSIX reservation refs must target annotated tag SHAs.'
        }
        [pscustomobject]@{ ref = $ref; sha = $sha; counter = $counter }
    }
}

function Assert-MsixAnnotatedTag {
    param([object]$Tag, [string]$TagSha, [string]$Ref, [string]$Repository)

    $sha = Get-MsixRequiredProperty $Tag 'sha'
    $name = Get-MsixRequiredProperty $Tag 'tag'
    $target = Get-MsixRequiredProperty $Tag 'object'
    $type = Get-MsixRequiredProperty $target 'type'
    $commit = Get-MsixRequiredProperty $target 'sha'
    $message = Get-MsixRequiredProperty $Tag 'message'
    if ($sha -isnot [string] -or $sha -cnotmatch '\A[0-9a-f]{40}\z' -or $sha -cne $TagSha -or
        $name -isnot [string] -or $name -cne $Ref.Substring('refs/tags/'.Length) -or
        $type -isnot [string] -or $type -cne 'commit' -or
        $commit -isnot [string] -or $commit -cnotmatch '\A[0-9a-f]{40}\z' -or $message -isnot [string]) {
        throw 'MSIX annotated tag response does not match its SHA, name, or commit target.'
    }
    try { $record = ConvertFrom-MsixJsonObject $message }
    catch { throw 'MSIX annotated tag message is not valid allocation JSON.' }
    $info = Assert-MsixVersionInfo -VersionInfo $record -SourceCommit $commit -RequireReserved
    if ($info.reservationRef -cne $Ref -or $info.repository -cne $Repository) {
        throw 'MSIX annotated allocation does not match its reservation ref or repository.'
    }
    return $info
}

function New-MsixVersionInfo {
    param([object]$App, [int]$Counter, [string]$SourceVersion, [string]$SourceCommit,
        [string]$SourceRef, [string]$Repository, [switch]$Reserve)

    $packageBase = '{0}.{1}.{2}' -f $App.major, $App.minor, $Counter
    $record = [pscustomobject][ordered]@{
        schemaVersion = 1
        sourceVersion = $SourceVersion
        sourceCommit = $SourceCommit
        sourceRef = $SourceRef
        repository = $Repository
        baseVersion = $App.baseVersion
        packageBaseVersion = $packageBase
        storePackageVersion = "$packageBase.0"
        packagingRevision = $Counter - $App.firstCounter
        allocation = if ($Reserve) { 'reserved' } else { 'preview' }
        reservationRef = if ($Reserve) { "refs/tags/msix-package/$($App.baseVersion)/$Counter" } else { $null }
    }
    Assert-MsixVersionInfo -VersionInfo $record -SourceCommit $SourceCommit -SourceVersion $SourceVersion
}

function Resolve-MsixPackageVersion {
    <#
    .SYNOPSIS
        Previews or durably reserves the next MSIX version for a numeric app base.
    .DESCRIPTION
        Official tags share one counter across prerelease, stable, and correction
        versions. Reservations are immutable annotated Git tags. Failed builds
        still consume their reservation; identical source reruns reuse it.
        Reserve verifies the exact existing source tag resolves to SourceCommit
        before each allocation attempt, peeling at most eight annotated tags.
        Preview is read-only and returns the next unallocated number, which may
        change on reruns after intervening official reservations.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceVersion,
        [Parameter(Mandatory)][string]$SourceCommit,
        [Parameter(Mandatory)][string]$SourceRef,
        [Parameter(Mandatory)][string]$Repository,
        [switch]$Reserve,
        [string]$GitHubToken = $env:GH_TOKEN,
        [string]$BaselinePath = (Join-Path $PSScriptRoot '..\.github\msix-version-baseline.json')
    )

    $app = Get-MsixAppVersion $SourceVersion
    Assert-MsixProvenance $SourceCommit $SourceRef $Repository
    $SourceCommit = $SourceCommit.ToLowerInvariant()
    if ($Reserve -and $SourceRef -cne "refs/tags/v$SourceVersion") {
        throw 'Reserved sourceRef must exactly match refs/tags/v plus sourceVersion.'
    }
    if ($Reserve -and [string]::IsNullOrWhiteSpace($GitHubToken)) { throw 'A GitHub token is required to reserve an MSIX version.' }
    if ($GitHubToken -match '\p{Cc}') { throw 'GitHub token must not contain control characters.' }
    $baseline = Read-MsixBaseline -Path $BaselinePath -App $app
    $cache = @{}
    $collisionRef = $null
    # Eight writes maximum. The final read still recognizes a successful competing duplicate run.
    for ($attempt = 0; $attempt -le 8; $attempt++) {
        if ($Reserve) {
            Assert-MsixSourceTag -Repository $Repository -SourceRef $SourceRef -SourceCommit $SourceCommit -Token $GitHubToken
        }
        $refs = @(Get-MsixReservationRefs -Repository $Repository -App $app -Token $GitHubToken | Sort-Object counter)
        if ($null -ne $collisionRef -and @($refs | Where-Object { $_.ref -ceq $collisionRef }).Count -ne 1) {
            throw 'MSIX create-ref conflict had no competing reservation; refusing to retry the API failure.'
        }
        $last = $baseline
        foreach ($ref in $refs) { $last = [Math]::Max($last, $ref.counter) }
        $inspect = if ($Reserve) { $refs } else { @($refs | Select-Object -Last 1) }
        $records = @(
            foreach ($ref in $inspect) {
                if (-not $cache.ContainsKey($ref.sha)) {
                    $tag = Invoke-MsixGitHubApi -Method GET -Repository $Repository -Path "tags/$($ref.sha)" -Token $GitHubToken
                    $cache[$ref.sha] = Assert-MsixAnnotatedTag -Tag $tag -TagSha $ref.sha -Ref $ref.ref -Repository $Repository
                }
                $record = $cache[$ref.sha]
                if ($record.reservationRef -cne $ref.ref) { throw 'An MSIX annotated tag was reused for a different reservation ref.' }
                $record
            }
        )
        if ($Reserve) {
            $sources = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($record in $records) {
                if (-not $sources.Add($record.sourceRef)) { throw 'MSIX reservations contain duplicate sourceRef allocations.' }
            }
            $existing = @($records | Where-Object { $_.sourceRef -ceq $SourceRef })
            if ($existing.Count -eq 1) {
                if ($existing[0].sourceCommit -cne $SourceCommit -or $existing[0].sourceVersion -cne $SourceVersion) {
                    throw 'The source tag moved or its version changed after MSIX reservation.'
                }
                return $existing[0]
            }
        }
        if ($last -ge $app.lastCounter) { throw 'MSIX allocation range exhausted for this app base; advance the app version.' }
        $info = New-MsixVersionInfo -App $app -Counter ($last + 1) -SourceVersion $SourceVersion `
            -SourceCommit $SourceCommit -SourceRef $SourceRef -Repository $Repository -Reserve:$Reserve
        if (-not $Reserve) { return $info }
        if ($attempt -eq 8) { throw 'MSIX reservation retry limit exhausted after eight competing allocations.' }

        $tagBody = @{
            tag = $info.reservationRef.Substring('refs/tags/'.Length)
            message = ($info | ConvertTo-Json -Depth 10 -Compress)
            object = $SourceCommit
            type = 'commit'
        }
        $tag = Invoke-MsixGitHubApi -Method POST -Repository $Repository -Path 'tags' -Token $GitHubToken -Body $tagBody
        $tagSha = Get-MsixRequiredProperty $tag 'sha'
        $created = Assert-MsixAnnotatedTag -Tag $tag -TagSha $tagSha -Ref $info.reservationRef -Repository $Repository
        if (($created | ConvertTo-Json -Compress) -cne ($info | ConvertTo-Json -Compress)) {
            throw 'MSIX create-tag response changed the requested allocation record.'
        }
        try {
            $createdRef = Invoke-MsixGitHubApi -Method POST -Repository $Repository -Path 'refs' -Token $GitHubToken `
                -Body @{ ref = $info.reservationRef; sha = $tagSha }
        }
        catch {
            $status = $_.Exception.Data['MsixHttpStatus']
            if ($status -ne 409 -and $status -ne 422) { throw }
            $collisionRef = $info.reservationRef
            continue
        }
        $refName = Get-MsixRequiredProperty $createdRef 'ref'
        $refObject = Get-MsixRequiredProperty $createdRef 'object'
        $refType = Get-MsixRequiredProperty $refObject 'type'
        $refSha = Get-MsixRequiredProperty $refObject 'sha'
        if ($refName -isnot [string] -or $refName -cne $info.reservationRef -or
            $refType -isnot [string] -or $refType -cne 'tag' -or
            $refSha -isnot [string] -or $refSha -cne $tagSha) {
            throw 'MSIX create-ref response does not match the requested annotated reservation.'
        }
        return $info
    }
}
