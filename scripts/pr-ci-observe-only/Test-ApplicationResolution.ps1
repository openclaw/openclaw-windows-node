# Data/metadata-only controls. Dummy .exe files are never executed; no owned/native process is opened.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PrivateRoot,
    [Parameter(Mandatory)][string]$ProductRoot,
    [Parameter(Mandatory)][string]$EntrypointPath,
    [Parameter(Mandatory)][scriptblock]$CanonicalPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$caseCount = 0
function Assert-Resolver([bool]$Value) { if (-not $Value) { throw 'application-resolution-selftest' } }
function Case([scriptblock]$Action) { & $Action; $script:caseCount++ }
function LegacyScalar([string]$Exe) { return $Exe }
if (Test-Path -LiteralPath $PrivateRoot) { throw 'resolver-selftest-root-exists' }
$null = New-Item -ItemType Directory -Path $PrivateRoot
try {
    $first = Join-Path $PrivateRoot 'first executable.exe'
    $second = Join-Path $PrivateRoot 'second executable.exe'
    [IO.File]::WriteAllBytes($first, [byte[]]@())
    [IO.File]::WriteAllBytes($second, [byte[]]@())
    $firstInfo = @(Get-Command -Name $first -CommandType Application -TotalCount 1 -ErrorAction Stop)
    $secondInfo = @(Get-Command -Name $second -CommandType Application -TotalCount 1 -ErrorAction Stop)
    $audit = @{ calls = 0; last = $null }
    $checkedCanonical = { param([string]$Path) $audit.calls++; $audit.last = $Path; & $CanonicalPath $Path }.GetNewClosure()
    function With-Source($Value) {
        $info = @(Get-Command -Name $first -CommandType Application -TotalCount 1 -ErrorAction Stop)[0]
        $info.PSObject.Properties.Remove('Source')
        $info.PSObject.Properties.Add([System.Management.Automation.PSNoteProperty]::new('Source', $Value))
        Assert-Resolver ($info -is [System.Management.Automation.ApplicationInfo])
        return $info
    }
    function Rejected($Candidate, [string]$Code) {
        $prior = $audit.calls; $rejected = $false
        try { $null = Select-ObserveApplicationSource -Candidates $Candidate -CanonicalPath $checkedCanonical }
        catch { Assert-Resolver ($_.Exception.Message -ceq $Code); $rejected = $true }
        Assert-Resolver $rejected
        Assert-Resolver ($audit.calls -eq $prior) # Rejected before the typed Canonical/Run boundaries.
    }
    Case {
        Assert-Resolver ($firstInfo.Count -eq 1 -and $secondInfo.Count -eq 1)
        Assert-Resolver ($firstInfo[0] -is [System.Management.Automation.ApplicationInfo] -and $secondInfo[0] -is [System.Management.Automation.ApplicationInfo])
        Assert-Resolver ($firstInfo[0].Source -ceq $first -and $secondInfo[0].Source -ceq $second)
    }
    Case {
        $selected = @(Select-ObserveApplicationSource -Candidates $firstInfo -CanonicalPath $checkedCanonical)
        Assert-Resolver ($selected.Count -eq 1 -and $selected[0] -is [string] -and $selected[0] -ceq $first)
        Assert-Resolver ($audit.calls -eq 1 -and $audit.last -ceq $first)
    }
    Case {
        $OFS = ' ' # Local PowerShell variable only. No environment or parent-scope mutation.
        $old = LegacyScalar @($first, $second)
        Assert-Resolver ($old -is [string] -and $old -ceq ($first + ' ' + $second))
        Rejected @($firstInfo[0], $secondInfo[0]) 'application-cardinality'
    }
    Case { Rejected $null 'application-cardinality' }
    Case { Rejected @() 'application-cardinality' }
    Case { Rejected @($firstInfo[0], $secondInfo[0]) 'application-cardinality' }
    Case { Rejected ([pscustomobject]@{ CommandType = 'Application'; Source = $first }) 'application-type' }
    Case {
        $arraySource = With-Source @($first, $second)
        Assert-Resolver ($arraySource.Source -is [array])
        Rejected $arraySource 'application-source-shape'
    }
    Case { Rejected (With-Source ('"' + $first + '"')) 'application-source-shape' }
    Case { Rejected (With-Source '.\relative.exe') 'application-source-shape' }
    Case { Rejected (With-Source (Join-Path $PrivateRoot 'missing.exe')) 'application-source-file' }
    Case { Rejected (With-Source $PrivateRoot) 'application-source-file' }
    Case { Rejected (With-Source ($first + [char]10)) 'application-source-shape' }
    Case { Rejected (With-Source $null) 'application-source-shape' }
    Case { Rejected (With-Source '') 'application-source-shape' }
    Case { Rejected (With-Source 17) 'application-source-shape' }
    Case {
        $rejected = $false
        try { $null = Select-ObserveApplicationSource -Candidates (With-Source $first) -CanonicalPath { throw 'synthetic-canonical-refusal' } }
        catch { Assert-Resolver ($_.Exception.Message -ceq 'synthetic-canonical-refusal'); $rejected = $true }
        Assert-Resolver $rejected # Propagation only; not a claim that a Windows reparse point was created.
    }
    Case {
        $entry = Get-Content -LiteralPath $EntrypointPath -Raw
        $headers = [regex]::Matches($entry, '(?m)^function Run\(([^\r\n]+)\) \{\r?$')
        Assert-Resolver ($headers.Count -eq 1)
        $parameters = $headers[0].Groups[1].Value
        Assert-Resolver ($parameters -ceq '[string]$Exe,[string[]]$Arguments,[int]$Seconds=900,[switch]$AllowFailure')
        $calls = [regex]::Matches($entry, '(?m)^[ \t]*\$r=(Run \$git @\(''-C'',\$pair\[0\],''rev-parse'',''HEAD''\) 30)\r?$')
        Assert-Resolver ($calls.Count -eq 1)
        $forward = [regex]::Escape('$r=[OpenClawObserve.OwnedCommand]::Run($Exe,$Arguments,$ProductRoot,$script:child,$private,$Seconds,$script:commandWitness)')
        Assert-Resolver ([regex]::Matches($entry, '(?m)^[ \t]*' + $forward + '\r?$').Count -eq 1)
        # Bind the guarded actual argument expression to the actual typed parameters, but only
        # through a data-only ScriptBlock. Never resolve or invoke the real Run/native owner.
        $binding = [scriptblock]::Create('param(' + $parameters + ') [pscustomobject]@{ exe=$Exe; argv=$Arguments; cwd=$ProductRoot; seconds=$Seconds; allowFailure=[bool]$AllowFailure }')
        $arguments = $calls[0].Groups[1].Value.Substring('Run '.Length)
        $invoke = [scriptblock]::Create('param($capture,$git,$pair,$ProductRoot) & $capture ' + $arguments)
        $pair = @($ProductRoot, 'synthetic-source-role', 'ProductHead')
        $resolved = Select-ObserveApplicationSource -Candidates (With-Source $first) -CanonicalPath $checkedCanonical
        $result = @(& $invoke $binding $resolved $pair $ProductRoot)
        Assert-Resolver ($result.Count -eq 1 -and $result[0].exe -ceq $first)
        Assert-Resolver ($result[0].argv -is [string[]] -and $result[0].argv.Count -eq 4)
        Assert-Resolver ($result[0].argv[0] -ceq '-C' -and $result[0].argv[1] -ceq $ProductRoot -and $result[0].argv[2] -ceq 'rev-parse' -and $result[0].argv[3] -ceq 'HEAD')
        Assert-Resolver ($result[0].cwd -ceq $ProductRoot -and $result[0].seconds -eq 30 -and -not $result[0].allowFailure)
    }
    # Independent golden strings exercise the actual already-loaded C# Quote, with no P/Invoke.
    $golden = @(
        @{ value = ''; expected = '""' },
        @{ value = 'plain'; expected = '"plain"' },
        @{ value = 'C:\Program Files\Git\cmd\git.exe'; expected = '"C:\Program Files\Git\cmd\git.exe"' },
        @{ value = 'a"b'; expected = '"a\"b"' },
        @{ value = 'C:\Tools\git.exe'; expected = '"C:\Tools\git.exe"' },
        @{ value = 'C:\with space\'; expected = '"C:\with space\\"' },
        @{ value = 'a\"b'; expected = '"a\\\"b"' },
        @{ value = 'C:\工具\git.exe'; expected = '"C:\工具\git.exe"' }
    )
    foreach ($case in $golden) { Case { Assert-Resolver ([OpenClawObserve.OwnedCommand]::Quote($case.value) -ceq $case.expected) } }
} finally {
    Remove-Item -LiteralPath $PrivateRoot -Recurse -Force
}
[ordered]@{ schema = 1; cases = $caseCount; passed = $true; nativeProcessesOpened = 0 }
