param(
    [string]$AttRoot = (Join-Path $PSScriptRoot '..\AviUtl2 ATT'),
    [switch]$IncludeModels
)

$ErrorActionPreference = 'Stop'
$AttRoot = (Resolve-Path $AttRoot).Path
$attRuntime = Join-Path $AttRoot 'runtime'
$runtime = Join-Path $PSScriptRoot 'runtime'

function Copy-FolderContent([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { throw "Required ATT asset directory was not found: $Source" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item (Join-Path $Source '*') $Destination -Recurse -Force
}

$attPython = Join-Path $attRuntime 'python-env\Scripts\python.exe'
$attFfmpeg = Join-Path $attRuntime 'ffmpeg\ffmpeg.exe'
$attFfprobe = Join-Path $attRuntime 'ffmpeg\ffprobe.exe'
if (-not (Test-Path $attPython)) { throw "ATT Python runtime is incomplete: $attPython was not found. Restore or reinstall ATT runtime before syncing FAT." }
if (-not (Test-Path $attFfmpeg) -or -not (Test-Path $attFfprobe)) { throw "ATT FFmpeg runtime is incomplete: ffmpeg.exe and ffprobe.exe must exist under $attRuntime\ffmpeg." }

New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Copy-FolderContent (Join-Path $AttRoot 'python') (Join-Path $runtime 'python')
# FAT owns fat_worker.py and fat_engine. Copy it last so an ATT asset can never
# replace the current JSON Lines protocol implementation in a release runtime.
Copy-FolderContent (Join-Path $PSScriptRoot 'python') (Join-Path $runtime 'python')
Copy-FolderContent (Join-Path $attRuntime 'ffmpeg') (Join-Path $runtime 'ffmpeg')
Copy-FolderContent (Join-Path $attRuntime 'python-env') (Join-Path $runtime 'python-env')
if ($IncludeModels) { Copy-FolderContent (Join-Path $attRuntime 'models') (Join-Path $runtime 'models') }
Write-Host "ATT runtime assets were synchronized to FAT: $runtime"
