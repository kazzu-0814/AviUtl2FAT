param(
    [ValidateSet('gemma-4-e2b-it', 'gemma-4-e4b-it', 'gemma-4-12b-it', 'gemma-4-26b-a4b-it')]
    [string]$ModelId = 'gemma-4-e2b-it',
    [switch]$ProbeWeight,
    [switch]$Download
)

$ErrorActionPreference = 'Stop'
$runtime = Split-Path -Parent $PSScriptRoot
$python = Join-Path $runtime 'python-runtime\python.exe'
$diagnostic = Join-Path $PSScriptRoot 'gemma_diagnostics.py'
$modelDirectory = Join-Path $runtime 'models'

if (-not (Test-Path -LiteralPath $python)) { throw "AltFactor Portable Python was not found: $python" }
if (-not (Test-Path -LiteralPath $diagnostic)) { throw "Gemma diagnostic was not found: $diagnostic" }

$arguments = @($diagnostic, '--model-id', $ModelId, '--model-dir', $modelDirectory)
if ($ProbeWeight) { $arguments += '--probe-weight' }
if ($Download) { $arguments += '--download' }

# Authentication is obtained by huggingface_hub from its normal secure token
# store.  A token is never accepted on this command line or written to output.
& $python @arguments
$exitCode = $LASTEXITCODE

if (-not $Download) {
    Write-Host ''
    Write-Host 'This was a diagnostic only. To explicitly retry the full download:'
    Write-Host ".\Test-AltFactorGemma.ps1 -ModelId $ModelId -Download"
}
exit $exitCode
