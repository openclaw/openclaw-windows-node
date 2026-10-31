param(
    [Parameter(Mandatory)][string]$WorkerPath,
    [Parameter(Mandatory)][string]$Family,
    [Parameter(Mandatory)][string]$ReadyName,
    [Parameter(Mandatory)][string]$ReleaseName,
    [Parameter(Mandatory)][string]$FinishName,
    [Parameter(Mandatory)][string]$ExternalPidFile,
    [Parameter(Mandatory)][string]$ResultPath
)
$ErrorActionPreference='Stop'
$ObservationReleaseTimeoutMilliseconds=10000
$ready=$null
$release=$null
$finish=$null
$exitCode=1
$result=[ordered]@{
    Pid=$PID;Family=$Family;Stage='open-handles';Succeeded=$false;ExitCode=$null
    LastNativeExitAfterWorker=$null;ErrorType=$null;Error=$null
}
try {
    $ready=[Threading.EventWaitHandle]::OpenExisting($ReadyName)
    $release=[Threading.EventWaitHandle]::OpenExisting($ReleaseName)
    $finish=[Threading.EventWaitHandle]::OpenExisting($FinishName)
    [IO.File]::WriteAllText($ExternalPidFile,[string]$PID)
    $result.Stage='await-observer-release'
    $result|ConvertTo-Json|Set-Content -LiteralPath $ResultPath -Encoding UTF8
    $ready.Set()|Out-Null
    if(!$release.WaitOne($ObservationReleaseTimeoutMilliseconds)){
        throw 'Observer did not release synchronized actual scheduled worker'
    }
    $result.Stage='production-worker'
    & $WorkerPath -Family $Family
    $succeeded=$?
    $result.LastNativeExitAfterWorker=$LASTEXITCODE
    # A successful PowerShell script need not execute a native command.
    if($succeeded){$exitCode=0}
    elseif($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0){$exitCode=[int]$LASTEXITCODE}
    else{$exitCode=1}
    $result.Succeeded=$succeeded
    $result.Stage='complete'
} catch {
    $result.ErrorType=$_.Exception.GetType().Name
    $result.Error=$_.Exception.Message
} finally {
    $result.ExitCode=$exitCode
    $result|ConvertTo-Json|Set-Content -LiteralPath $ResultPath -Encoding UTF8
    if($finish){$finish.Set()|Out-Null}
    foreach($handle in @($ready,$release,$finish)){if($handle){$handle.Dispose()}}
}
exit $exitCode
