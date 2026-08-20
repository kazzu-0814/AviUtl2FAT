param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$PythonHome = "$env:LOCALAPPDATA\Programs\Python\Python311",
    [string]$SpeechSitePackages = (Join-Path $PSScriptRoot '..\runtime\python-env\Lib\site-packages')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $PythonHome 'python.exe'))) { throw "Python 3.11 x64 was not found: $PythonHome" }
if (-not (Test-Path -LiteralPath $SpeechSitePackages)) { throw "Speech packages were not found: $SpeechSitePackages" }

# This deliberately creates a relocatable CPython layout. It does not copy a
# virtual environment, pyvenv.cfg, activation scripts, or a developer path.
if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Get-ChildItem -LiteralPath $PythonHome -Force | Where-Object { $_.Name -notin @('Doc', 'include', 'libs', 'Scripts') } | Copy-Item -Destination $Destination -Recurse -Force

$targetSitePackages = Join-Path $Destination 'Lib\site-packages'
New-Item -ItemType Directory -Force -Path $targetSitePackages | Out-Null
Get-ChildItem -LiteralPath $SpeechSitePackages -Force |
    Where-Object {
        $_.Name -notmatch '^(torch|transformers|accelerate|sympy|networkx|jinja2|pytest|pluggy|iniconfig|pygments|rich|typer|shellingham|markdown_it|mdurl)(-|$|\.)' -and
        $_.Name -notmatch '^(~ympy|__pycache__)$'
    } |
    Copy-Item -Destination $targetSitePackages -Recurse -Force

Remove-Item -LiteralPath (Join-Path $Destination 'pyvenv.cfg') -Force -ErrorAction SilentlyContinue
$python = Join-Path $Destination 'python.exe'
& $python -c "import av, ctranslate2, faster_whisper, numpy, onnxruntime; print('FAT portable speech runtime OK')"
if ($LASTEXITCODE -ne 0) { throw 'The generated portable Python runtime did not pass its import check.' }
