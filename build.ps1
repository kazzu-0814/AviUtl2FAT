param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [switch]$SyncAttAssets)
$ErrorActionPreference = 'Stop'
if ($SyncAttAssets) { & (Join-Path $PSScriptRoot 'sync-att-assets.ps1') }
dotnet restore (Join-Path $PSScriptRoot 'AviUtl2FAT.sln')
dotnet build (Join-Path $PSScriptRoot 'AviUtl2FAT.sln') -c $Configuration --no-restore
$cargoCandidates = @(
    (Get-Command cargo -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe')
) | Where-Object { $_ -and (Test-Path $_) }
if (-not $cargoCandidates) { throw 'cargo.exe was not found. Install Rust or add %USERPROFILE%\.cargo\bin to PATH.' }
& $cargoCandidates[0] build --manifest-path (Join-Path $PSScriptRoot 'plugin\Cargo.toml') --release
