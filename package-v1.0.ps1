param(
    [switch]$BuildPortablePython,
    [switch]$BuildInstaller,
    [switch]$SkipPortableArchive,
    [switch]$Clean,
    # Optional trusted code-signing certificate.  Do not use a self-signed
    # development certificate for public releases: Smart App Control requires
    # a certificate issued by a trusted provider.
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$SignToolPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.1.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
$version = $Version
$dist = Join-Path $root "dist\AviUtl2FAT-$version-x64"
$payload = Join-Path $dist 'Plugin\AviUtl2FAT'
$app = Join-Path $payload 'FAT'
$workerPublish = Join-Path $dist '_worker-publish'
$script:signTool = $null
$script:certificateStoreArgument = @()
$script:certificateThumbprint = $null
$script:timestampUrl = $TimestampUrl

function Initialize-CodeSigning([string]$RequestedThumbprint, [string]$RequestedTimestampUrl, [string]$RequestedSignToolPath) {
    if ([string]::IsNullOrWhiteSpace($RequestedThumbprint)) { return }

    $script:certificateThumbprint = ($RequestedThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($script:certificateThumbprint.Length -ne 40) {
        throw 'CertificateThumbprint must be a SHA-1 certificate thumbprint.'
    }

    $certificate = Get-ChildItem -Path "Cert:\CurrentUser\My\$script:certificateThumbprint" -ErrorAction SilentlyContinue
    if (-not $certificate) {
        $certificate = Get-ChildItem -Path "Cert:\LocalMachine\My\$script:certificateThumbprint" -ErrorAction SilentlyContinue
        if ($certificate) { $script:certificateStoreArgument = @('/sm') }
    }
    if (-not $certificate -or -not $certificate.HasPrivateKey) {
        throw 'The requested code-signing certificate with a private key was not found in CurrentUser\\My or LocalMachine\\My.'
    }
    if ([string]::IsNullOrWhiteSpace($RequestedTimestampUrl)) {
        throw 'TimestampUrl is required for a signed public release.'
    }
    $script:timestampUrl = $RequestedTimestampUrl

    $candidates = @(
        $RequestedSignToolPath,
        (Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        'C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe',
        'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    $script:signTool = $candidates | Select-Object -First 1
    if (-not $script:signTool) {
        throw 'signtool.exe was not found. Install the Windows SDK signing tools or pass -SignToolPath.'
    }
}

function Sign-PackageBinary([string]$Path) {
    if (-not $script:signTool) { return }
    & $script:signTool sign /fd SHA256 /sha1 $script:certificateThumbprint @script:certificateStoreArgument /tr $script:timestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed: $Path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { throw "Signature validation failed for ${Path}: $($signature.Status)" }
}

function Remove-PackageOutput([string]$Path) {
    # robocopy handles long paths more reliably than Copy-Item/Remove-Item.
    # Mirror an explicitly-created empty folder first, then remove the empty
    # output directory. This function is reached only with the explicit -Clean
    # switch, so normal packaging never deletes an existing build.
    $empty = Join-Path ([System.IO.Path]::GetTempPath()) ("AviUtl2FAT-empty-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    try {
        & robocopy $empty $Path /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -gt 7) { throw "Failed to clean package output: $Path" }
    }
    finally {
        Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

if (-not (Test-Path -LiteralPath $dotnet)) { throw ".NET SDK was not found: $dotnet" }
if (-not (Test-Path -LiteralPath $cargo)) { throw "Rust cargo.exe was not found: $cargo" }
$requestedThumbprint = [string]$PSBoundParameters['CertificateThumbprint']
$requestedTimestampUrl = if ($PSBoundParameters.ContainsKey('TimestampUrl')) { [string]$PSBoundParameters['TimestampUrl'] } else { $TimestampUrl }
$requestedSignToolPath = [string]$PSBoundParameters['SignToolPath']
Initialize-CodeSigning -RequestedThumbprint $requestedThumbprint -RequestedTimestampUrl $requestedTimestampUrl -RequestedSignToolPath $requestedSignToolPath
if (Test-Path -LiteralPath $dist) {
    if (-not $Clean) {
        throw "The package output already exists and was not changed: $dist`nChoose a new -Version (recommended), or explicitly pass -Clean to rebuild that exact version."
    }
    Remove-PackageOutput $dist
}
New-Item -ItemType Directory -Force -Path $app | Out-Null

# The App includes its isolated Worker. self-contained avoids a .NET Desktop
# Runtime prerequisite on the recipient's computer.
& $dotnet publish (Join-Path $root 'src\AviUtl2FAT.App\AviUtl2FAT.App.csproj') -c Release -r win-x64 --self-contained true -p:Version=$version -p:AssemblyVersion="$version.0" -p:FileVersion="$version.0" -p:InformationalVersion=$version -p:SkipFatWorkerDeployment=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Self-contained App publish failed.' }
& $dotnet publish (Join-Path $root 'src\AviUtl2FAT.Worker\AviUtl2FAT.Worker.csproj') -c Release -r win-x64 --self-contained true -p:Version=$version -p:AssemblyVersion="$version.0" -p:FileVersion="$version.0" -p:InformationalVersion=$version -p:DebugType=None -p:DebugSymbols=false -o $workerPublish
if ($LASTEXITCODE -ne 0) { throw 'Self-contained Worker publish failed.' }

# Publish the Worker into its own directory. Publishing it directly beside the
# App used to overwrite AviUtl2FAT.Core.dll with a stale Worker dependency,
# causing the App to crash before it could show its window.
foreach ($workerLaunchFile in @('AviUtl2FAT.Worker.exe', 'AviUtl2FAT.Worker.dll', 'AviUtl2FAT.Worker.deps.json', 'AviUtl2FAT.Worker.runtimeconfig.json')) {
    $source = Join-Path $workerPublish $workerLaunchFile
    if (-not (Test-Path -LiteralPath $source)) { throw "Worker publish validation failed: $workerLaunchFile" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $app $workerLaunchFile) -Force
}
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
$required = @('AviUtl2FAT.aux2', 'FAT\AviUtl2FAT.App.exe', 'FAT\AviUtl2FAT.App.dll', 'FAT\AviUtl2FAT.Core.dll', 'FAT\AviUtl2FAT.Worker.exe', 'FAT\AviUtl2FAT.Worker.dll', 'FAT\runtime\python\fat_worker.py', 'FAT\runtime\ffmpeg\ffmpeg.exe', 'FAT\runtime\ffmpeg\ffprobe.exe', 'FAT\licenses\GPL-3.0.txt', 'FAT\FFMPEG_PROVENANCE.md')
if ($BuildPortablePython) { $required += 'FAT\runtime\python-runtime\python.exe' }
foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $payload $relative))) { throw "Package validation failed: $relative" } }

$expectedAssemblyVersion = [Version]::Parse("$version.0")
$appAssembly = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $app 'AviUtl2FAT.App.dll'))
$coreAssembly = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $app 'AviUtl2FAT.Core.dll'))
if ($appAssembly.Version -ne $expectedAssemblyVersion -or $coreAssembly.Version -ne $expectedAssemblyVersion) {
    throw "Package assembly validation failed: App=$($appAssembly.Version), Core=$($coreAssembly.Version), expected=$expectedAssemblyVersion"
}

if ($script:signTool) {
    # Smart App Control evaluates the executable code that FAT loads, not just
    # the outer installer. Sign all PE payload binaries before archiving.
    Get-ChildItem -LiteralPath $payload -Recurse -File |
        Where-Object { $_.Extension -in '.exe', '.dll', '.pyd', '.aux2' } |
        ForEach-Object { Sign-PackageBinary $_.FullName }
}

if (-not $SkipPortableArchive) {
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath (Join-Path $root "dist\AviUtl2FAT-$version-x64-portable.zip") -Force
}
if ($BuildInstaller) {
    $iscc = @('C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe', (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 is required to build the installer. Install it, then run this command again.' }
    $innoArguments = @((Join-Path $root 'release\AviUtl2FAT.iss'), "/DSourcePayload=$payload", "/DAppVersion=$version")
    if ($script:signTool) {
        # Inno Setup signs both the outer setup executable and its generated
        # uninstaller through this named SignTool definition.
        $innoSignCommand = ('"{0}" sign /fd SHA256 /sha1 {1} {2} /tr {3} /td SHA256 $f' -f $script:signTool, $script:certificateThumbprint, ($script:certificateStoreArgument -join ' '), $script:timestampUrl)
        $innoArguments += '/DSignToolName=fatcodesign'
        $innoArguments += "/Sfatcodesign=$innoSignCommand"
    }
    & $iscc @innoArguments
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup build failed.' }
    if ($script:signTool) {
        $installer = Join-Path $root "dist\AviUtl2FAT-Setup-$version-x64.exe"
        $installerSignature = Get-AuthenticodeSignature -LiteralPath $installer
        if ($installerSignature.Status -ne 'Valid') { throw "Installer signature validation failed: $($installerSignature.Status)" }
    }
}
[pscustomobject]@{ Product = 'AviUtl2 FAT'; Version = $version; Payload = $payload; SelfContainedDotNet = $true; PortablePython = [bool]$BuildPortablePython; ModelsBundled = $false; CodeSigned = [bool]$script:signTool } | ConvertTo-Json
