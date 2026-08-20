param(
    [switch]$BuildPortablePython,
    [switch]$BuildInstaller,
    [switch]$Clean,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.2'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
$version = $Version
$dist = Join-Path $root "dist\AviUtl2FAT-$version-x64"
$payload = Join-Path $dist 'Plugin\AviUtl2FAT'
$app = Join-Path $payload 'FAT'

if (-not (Test-Path -LiteralPath $dotnet)) { throw ".NET SDK was not found: $dotnet" }
if (-not (Test-Path -LiteralPath $cargo)) { throw "Rust cargo.exe was not found: $cargo" }
if (Test-Path -LiteralPath $dist) {
    if (-not $Clean) {
        throw "The package output already exists and was not changed: $dist`nChoose a new -Version (recommended), or explicitly pass -Clean to rebuild that exact version."
    }
    Remove-Item -LiteralPath $dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $app | Out-Null

# The App includes its isolated Worker. self-contained avoids a .NET Desktop
# Runtime prerequisite on the recipient's computer.
& $dotnet publish (Join-Path $root 'src\AviUtl2FAT.App\AviUtl2FAT.App.csproj') -c Release -r win-x64 --self-contained true -p:Version=$version -p:BuildProjectReferences=false -p:SkipFatWorkerDeployment=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Self-contained App publish failed.' }
& $dotnet publish (Join-Path $root 'src\AviUtl2FAT.Worker\AviUtl2FAT.Worker.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Self-contained Worker publish failed.' }
& $cargo build --manifest-path (Join-Path $root 'plugin\Cargo.toml') --release
if ($LASTEXITCODE -ne 0) { throw 'Rust plugin release build failed.' }
Copy-Item -LiteralPath (Join-Path $root 'plugin\target\release\aviutl2_fat_plugin.dll') -Destination (Join-Path $payload 'AviUtl2FAT.aux2') -Force
Copy-Item -LiteralPath (Join-Path $root 'plugin\package.txt') -Destination $payload -Force

$runtime = Join-Path $app 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'runtime\python') -Destination $runtime -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'runtime\ffmpeg') -Destination $runtime -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $app 'licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'release\licenses\GPL-3.0.txt') -Destination (Join-Path $app 'licenses\GPL-3.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'release\FFMPEG_PROVENANCE.md') -Destination (Join-Path $app 'FFMPEG_PROVENANCE.md') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $runtime 'models') | Out-Null
if ($BuildPortablePython) {
    & (Join-Path $root 'release\New-PortablePythonRuntime.ps1') -Destination (Join-Path $runtime 'python-runtime')
}

foreach ($document in @('README.md', 'QUICKSTART.md', 'LICENSE', 'THIRD_PARTY_NOTICES.txt', 'RELEASE_CHECKLIST.md', 'release\FFMPEG_PROVENANCE.md', 'release\licenses\GPL-3.0.txt')) {
    Copy-Item -LiteralPath (Join-Path $root $document) -Destination $dist -Force
}
$required = @('AviUtl2FAT.aux2', 'FAT\AviUtl2FAT.App.exe', 'FAT\AviUtl2FAT.Worker.exe', 'FAT\runtime\python\fat_worker.py', 'FAT\runtime\ffmpeg\ffmpeg.exe', 'FAT\runtime\ffmpeg\ffprobe.exe', 'FAT\licenses\GPL-3.0.txt', 'FAT\FFMPEG_PROVENANCE.md')
if ($BuildPortablePython) { $required += 'FAT\runtime\python-runtime\python.exe' }
foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $payload $relative))) { throw "Package validation failed: $relative" } }

Compress-Archive -Path (Join-Path $dist '*') -DestinationPath (Join-Path $root "dist\AviUtl2FAT-$version-x64-portable.zip") -Force
if ($BuildInstaller) {
    $iscc = @('C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe', (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 is required to build the installer. Install it, then run this command again.' }
    & $iscc (Join-Path $root 'release\AviUtl2FAT.iss') "/DSourcePayload=$payload" "/DAppVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup build failed.' }
}
[pscustomobject]@{ Product = 'AviUtl2 FAT'; Version = $version; Payload = $payload; SelfContainedDotNet = $true; PortablePython = [bool]$BuildPortablePython; ModelsBundled = $false } | ConvertTo-Json
