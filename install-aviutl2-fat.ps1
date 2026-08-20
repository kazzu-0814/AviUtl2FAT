param(
    [string]$TargetRoot = 'C:\ProgramData\aviutl2\Plugin',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipRuntime,
    [switch]$UseExistingPackage
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not (Test-Path -LiteralPath $TargetRoot)) { throw "AviUtl2 Plugin folder was not found: $TargetRoot" }
if (Get-Process aviutl2 -ErrorAction SilentlyContinue) { throw 'AviUtl2 is running. Close AviUtl2 before deployment.' }

$source = Join-Path $root 'dist\AviUtl2-FAT-v0.9.8\Plugin\AviUtl2FAT'
if (-not $UseExistingPackage) {
    & (Join-Path $root 'package-v0.9.ps1') -Configuration $Configuration -IncludeRuntime:(-not $SkipRuntime)
}
if (-not (Test-Path -LiteralPath $source)) { throw "FAT package was not found: $source" }
$destination = Join-Path $TargetRoot 'AviUtl2FAT'
$backupRoot = Join-Path $root 'backup\aviutl2-fat'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'

if (Test-Path -LiteralPath $destination) {
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    Copy-Item -LiteralPath $destination -Destination (Join-Path $backupRoot $stamp) -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force

# A previous development deployment was a loose .aux2 directly in Plugin.
# Preserve it in the project backup instead of allowing two FAT plugins to load.
$legacy = Get-ChildItem -LiteralPath $TargetRoot -File -Filter 'aviutl2_fat_plugin*.aux2'
foreach ($file in $legacy) {
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    Move-Item -LiteralPath $file.FullName -Destination (Join-Path $backupRoot ("$stamp-" + $file.Name)) -Force
}

$required = @('AviUtl2FAT.aux2', 'FAT\AviUtl2FAT.App.exe', 'FAT\AviUtl2FAT.Worker.exe', 'FAT\AviUtl2FAT.Worker.dll', 'FAT\AviUtl2FAT.Worker.deps.json', 'FAT\AviUtl2FAT.Worker.runtimeconfig.json', 'FAT\AviUtl2FAT.App.runtimeconfig.json')
foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $destination $relative))) { throw "Deployment validation failed: $relative" } }
if (-not $SkipRuntime -and -not (Test-Path -LiteralPath (Join-Path $destination 'FAT\runtime\python-env\Scripts\python.exe'))) { throw 'Deployment validation failed: FAT runtime Python is missing.' }

[pscustomobject]@{
    Installed = $destination
    Menu = '編集 → プラグイン → AviUtl2 FAT → FAT を開く'
    RuntimeIncluded = -not $SkipRuntime
    Backup = if (Test-Path -LiteralPath $backupRoot) { $backupRoot } else { $null }
} | ConvertTo-Json
