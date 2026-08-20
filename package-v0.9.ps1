param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$IncludeRuntime
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw ".NET SDK was not found: $dotnet" }
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) { throw "Rust cargo.exe was not found: $cargo" }

$dist = Join-Path $root 'dist\AviUtl2-FAT-v0.9.8'
$payload = Join-Path $dist 'Plugin\AviUtl2FAT'
$app = Join-Path $payload 'FAT'
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app | Out-Null

& $dotnet publish (Join-Path $root 'src\AviUtl2FAT.App\AviUtl2FAT.App.csproj') -c $Configuration -r win-x64 --self-contained false -o $app
# Framework-dependent workers require their matching DLL, deps and runtimeconfig
# beside the executable; publish the full worker launch set into the FAT folder.
& $dotnet publish (Join-Path $root 'src\AviUtl2FAT.Worker\AviUtl2FAT.Worker.csproj') -c $Configuration -r win-x64 --self-contained false -o $app
& $cargo build --manifest-path (Join-Path $root 'plugin\Cargo.toml') --release
Copy-Item -LiteralPath (Join-Path $root 'plugin\target\release\aviutl2_fat_plugin.dll') -Destination (Join-Path $payload 'AviUtl2FAT.aux2') -Force
Copy-Item -LiteralPath (Join-Path $root 'plugin\package.txt') -Destination $payload -Force

if ($IncludeRuntime) {
    $runtime = Join-Path $root 'runtime'
    if (-not (Test-Path -LiteralPath (Join-Path $runtime 'python-env\Scripts\python.exe'))) { throw 'FAT runtime is incomplete: runtime\\python-env\\Scripts\\python.exe was not found.' }
    if (-not (Test-Path -LiteralPath (Join-Path $runtime 'python\fat_worker.py'))) { throw 'FAT runtime is incomplete: runtime\\python\\fat_worker.py was not found.' }
    Copy-Item -LiteralPath $runtime -Destination (Join-Path $app 'runtime') -Recurse -Force
    Get-ChildItem -LiteralPath (Join-Path $app 'runtime') -Directory -Filter 'python-env.incomplete-*' | Remove-Item -Recurse -Force
}

$required = @('AviUtl2FAT.aux2', 'FAT\AviUtl2FAT.App.exe', 'FAT\AviUtl2FAT.Worker.exe', 'FAT\AviUtl2FAT.Worker.dll', 'FAT\AviUtl2FAT.Worker.deps.json', 'FAT\AviUtl2FAT.Worker.runtimeconfig.json', 'FAT\AviUtl2FAT.App.runtimeconfig.json')
foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $payload $relative))) { throw "Package validation failed: $relative" } }
if ($IncludeRuntime -and -not (Test-Path -LiteralPath (Join-Path $app 'runtime\python-env\Scripts\python.exe'))) { throw 'Package validation failed: runtime Python is missing.' }

[pscustomobject]@{ Product = 'AviUtl2 FAT'; Version = '0.9.8'; Payload = $payload; RuntimeIncluded = [bool]$IncludeRuntime } | ConvertTo-Json
