$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\..\scripts\PrivateDesktopSecurity.cs')
function Bytes([string]$Sddl){
    $sd=[Security.AccessControl.RawSecurityDescriptor]::new($Sddl)
    $b=[byte[]]::new($sd.BinaryLength);$sd.GetBinaryForm($b,0);return ,$b
}
function Require([bool]$Value,[string]$Message){if(!$Value){throw $Message};$script:checks++}
function IsLowNoWriteUp($Ace){
    # GetSddlForm(Audit) omits mandatory labels. Inspect the documented ACE layout.
    $b=[byte[]]::new($Ace.BinaryLength);$Ace.GetBinaryForm($b,0)
    return $b[0] -eq 0x11 -and [BitConverter]::ToUInt32($b,4) -eq 1 -and
        [Security.Principal.SecurityIdentifier]::new($b,8).Value -ceq 'S-1-16-4096'
}
$checks=0
$inputBytes=Bytes 'O:SYG:SYD:(A;;GA;;;SY)(A;;0x20141;;;AC)(A;;0x20141;;;S-1-15-2-2)'
$unchanged=[Convert]::ToBase64String($inputBytes)
$result=[PrivateDesktopSecurity]::CompleteNullCreatedDesktop($inputBytes)
$sd=[Security.AccessControl.RawSecurityDescriptor]::new($result,0)
Require ([Convert]::ToBase64String($inputBytes) -ceq $unchanged) 'Input descriptor was mutated'
Require ($sd.Owner.Value -ceq 'S-1-5-18' -and $sd.Group.Value -ceq 'S-1-5-18') 'Owner/group changed'
$added=@($sd.DiscretionaryAcl|Where-Object AccessMask -EQ 0x82)
Require ($sd.DiscretionaryAcl.Count -eq 5 -and $added.Count -eq 2 -and
    ($added.SecurityIdentifier.Value -contains 'S-1-15-2-1') -and
    ($added.SecurityIdentifier.Value -contains 'S-1-15-2-2')) 'Expected only the two minimal package write/create ACEs'
Require ($sd.SystemAcl.Count -eq 1 -and (IsLowNoWriteUp $sd.SystemAcl[0])) 'Missing Low no-write-up label'
$second=[PrivateDesktopSecurity]::CompleteNullCreatedDesktop($result)
Require ([Convert]::ToBase64String($second) -ceq [Convert]::ToBase64String($result)) 'Repeated completion added rights or changed the descriptor'
$partial=Bytes 'D:(A;;0x80;;;AC)(A;;0x2;;;S-1-15-2-2)'
$partialSd=[Security.AccessControl.RawSecurityDescriptor]::new([PrivateDesktopSecurity]::CompleteNullCreatedDesktop($partial),0)
Require ($partialSd.DiscretionaryAcl.Count -eq 4 -and
    $partialSd.DiscretionaryAcl[2].AccessMask -eq 2 -and
    $partialSd.DiscretionaryAcl[3].AccessMask -eq 0x80) 'Added already-granted or unrelated access'
$rejected=$false
try{[void][PrivateDesktopSecurity]::CompleteNullCreatedDesktop((Bytes 'D:(D;ID;0x80;;;WD)(A;;GA;;;SY)'))}catch{$rejected=$true}
Require $rejected 'Inherited write denial was bypassed'
$rejected=$false
try{[void][PrivateDesktopSecurity]::CompleteNullCreatedDesktop((Bytes 'D:NO_ACCESS_CONTROL'))}catch{$rejected=$true}
Require $rejected 'NULL DACL must not become an implicit broad grant'
$audit=Bytes 'D:(A;;GA;;;SY)S:(AU;SA;0x1;;;WD)(ML;;NW;;;ME)'
$auditSd=[Security.AccessControl.RawSecurityDescriptor]::new([PrivateDesktopSecurity]::CompleteNullCreatedDesktop($audit),0)
Require ($auditSd.SystemAcl.Count -eq 2 -and
    $auditSd.SystemAcl[0].AceType -eq [Security.AccessControl.AceType]::SystemAudit -and
    (IsLowNoWriteUp $auditSd.SystemAcl[1])) 'Audit ACE lost while completing the mandatory label'
"PASS: $checks fixture-only private desktop security regressions (no live object/token queries)."
