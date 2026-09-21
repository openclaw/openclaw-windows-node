# Exercise the production framing functions without touching registry or launching the host.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$errors = $null
$source = Join-Path $PSScriptRoot 'Test-BrowserNativeHost.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Native proof script did not parse.' }
foreach ($name in @('Write-Frame', 'Read-Frame')) {
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "Expected one $name function." }
    . ([ScriptBlock]::Create($definitions[0].Extent.Text))
}
$stream = [IO.MemoryStream]::new()
try {
    Write-Frame $stream ([Text.Encoding]::UTF8.GetBytes('{"ok":true,"marker":"native-framing-proof"}'))
    $stream.Position = 0
    $result = @(Read-Frame $stream)
    if ($result.Count -ne 1 -or -not $result[0].ok -or $result[0].marker -ne 'native-framing-proof') {
        throw 'Read-Frame must return exactly one decoded message, not async completion objects.'
    }
} finally { $stream.Dispose() }
Write-Host 'NATIVE_FRAMING_HELPER_PROOF_OK'
