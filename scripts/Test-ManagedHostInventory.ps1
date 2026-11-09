# Pure projection/literal tests. Never invokes the host-observation section.
$checks = 0
function Confirm([bool]$Condition) {
    if (-not $Condition) { throw 'inventory-self-test-failed' }
    $script:checks++
}
$SourceRevision = '0123456789abcdef0123456789abcdef01234567'
$HostSelector = 'azure-mxc-1'
$ExpectedArchitecture = 'x64'
Confirm (Test-SourceRevision $SourceRevision)
Confirm (-not (Test-SourceRevision ($SourceRevision + [Environment]::NewLine)))
Confirm (-not (Test-SourceRevision ('A' * 40)))
Confirm (-not (Test-SourceRevision ('g' * 40)))
Confirm (-not (Test-SourceRevision ('a' * 39)))
Confirm ((Convert-SafeVersion '10.0.26100') -ceq '10.0.26100')
Confirm ($null -eq (Convert-SafeVersion ('10.0' + [Environment]::NewLine)))
Confirm ($null -eq (Convert-SafeVersion '١٠.٠'))
Confirm ($null -eq (Convert-SafeVersion '10.0.PRIVATE_VALUE'))
Confirm ((Convert-SafeBuild '26100') -eq 26100)
Confirm ($null -eq (Convert-SafeBuild '99999999'))
Confirm ($null -eq (Convert-SafeBuild '26100-private'))
Confirm ((Convert-SafeArchitecture 'ARM64') -ceq 'arm64')
Confirm ((Convert-SafeArchitecture 'PRIVATE_HOST') -ceq 'unknown')
Confirm ($null -eq (Convert-SafeBoolean 'true'))
Confirm ((Convert-SafeBoolean $false) -ceq $false)

$report = New-InventoryReport '10.0.26100' '26100' ([uint32]1) 'X64' 'X64' $true $true $false
$json = Get-SafeJson $report
# Ordinary JSON quotes. Python static controls independently parse this literal too.
$expected = '{"schema":2,"sourceRevision":"0123456789abcdef0123456789abcdef01234567","hostSelector":"azure-mxc-1","outcome":"observed-with-limits","os":{"observed":true,"version":"10.0.26100","build":26100,"productType":1,"architecture":"x64","nativeProcessMatches":true},"session":{"userInteractive":true,"nonzeroSession":true,"unlocked":"unknown","restorableOwnedState":"unknown"},"wsl":{"systemExecutableExists":false,"wsl2":"unknown","distributions":"unknown","runningState":"unknown"},"mxcRuntime":"unknown","gatewayPath":"unknown","landingQualified":false}'
Confirm ($json -ceq $expected)
$roundTrip = ConvertFrom-Json -InputObject $json -AsHashtable
Confirm ($roundTrip.Count -eq 10 -and $roundTrip.os.Count -eq 6 -and $roundTrip.session.Count -eq 4 -and $roundTrip.wsl.Count -eq 4)
Confirm ($roundTrip.schema -eq 2 -and $roundTrip.os.productType -eq 1 -and $roundTrip.os.build -eq 26100)
Confirm ($roundTrip.os.observed -is [bool] -and $roundTrip.os.observed -eq $true)
Confirm ($roundTrip.wsl.systemExecutableExists -is [bool] -and $roundTrip.wsl.systemExecutableExists -eq $false)
Confirm ($roundTrip.landingQualified -is [bool] -and $roundTrip.landingQualified -eq $false)

$bad = New-InventoryReport 'PRIVATE_VERSION' 'PRIVATE_BUILD' 'PRIVATE_TYPE' 'PRIVATE_ARCH' 'PRIVATE_PROCESS' 'PRIVATE_SESSION' 'PRIVATE_SESSION' 'PRIVATE_PATH'
$badJson = Get-SafeJson $bad
Confirm ($badJson -cnotmatch 'PRIVATE')
Confirm ($bad.outcome -ceq 'incomplete' -and $null -eq $bad.os.version -and $null -eq $bad.os.build -and $null -eq $bad.os.productType)
Confirm ($null -eq $bad.session.userInteractive -and $null -eq $bad.session.nonzeroSession -and $null -eq $bad.wsl.systemExecutableExists)
Confirm ((New-InventoryReport '10.0.26100' '26100' 2 'X64' 'X64' $false $false $true).os.productType -eq 2)
Confirm ((New-InventoryReport '10.0.26100' '26100' 3 'X64' 'X64' $false $false $true).os.productType -eq 3)
Confirm ($null -eq (New-InventoryReport '10.0.26100' '26100' 4 'X64' 'X64' $false $false $true).os.productType)
Confirm ($null -eq (New-InventoryReport '10.0.26100' '26100' '1' 'X64' 'X64' $false $false $true).os.productType)
Confirm ((New-InventoryReport '10.0.26100' '26100' 1 'Arm64' 'X64' $true $true $true).outcome -ceq 'incomplete')
Confirm ((New-InventoryReport '10.0.26100' '26100' 1 'X64' 'X64' $true $true $true).wsl.wsl2 -ceq 'unknown')
$oversizeRejected = $false
try { $null = Get-SafeJson @{ data = ('x' * ($MaxJsonBytes + 1)) } } catch { $oversizeRejected = $true }
Confirm $oversizeRejected
$HostSelector = 'PRIVATE_HOST'
$inputRejected = $false
try { $null = New-InventoryReport '10.0.26100' '26100' 1 'X64' 'X64' $true $true $true } catch { $inputRejected = $true }
Confirm $inputRejected
Write-Output (Get-SafeJson @{ selfTest = 'passed'; checks = $checks })
