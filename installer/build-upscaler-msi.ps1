param(
    [string]$Version = "10.6.3.0",
    [switch]$SkipPublish,
    [switch]$DownloadLibMpv
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$deployDir = Join-Path $repoRoot "deploy-upscaler"
$outDir = Join-Path $PSScriptRoot "out"
$wxsPath = Join-Path $PSScriptRoot "Upscaler.wxs"
$iconPath = Join-Path $repoRoot "src\CDBurner10.Upscaler\Assets\app.ico"
$licensePath = Join-Path $PSScriptRoot "License.rtf"
$libMpvTarget = Join-Path $repoRoot "native\upscaler\libmpv-2.dll"
$shortVer = ($Version -split '\.')[0..2] -join '.'
$msiPath = Join-Path $outDir "CDBurner10-Upscaler-$shortVer-win-x64.msi"

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

if ($DownloadLibMpv -or (-not $SkipPublish -and -not (Test-Path $libMpvTarget))) {
    Write-Host "Downloading latest x64 libmpv-2.dll from zhongfly/mpv-winbuild..." -ForegroundColor Cyan
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/zhongfly/mpv-winbuild/releases/latest" -Headers @{ "User-Agent" = "CDBurner10-Upscaler-Build" }
    $asset = $release.assets | Where-Object { $_.name -match '^mpv-dev-x86_64-\d+.*\.7z$' } | Select-Object -First 1
    if (-not $asset) {
        $asset = $release.assets | Where-Object { $_.name -match '^mpv-dev-x86_64.*\.7z$' } | Select-Object -First 1
    }
    if (-not $asset) {
        throw "Could not find an x86_64 mpv-dev .7z asset in the latest zhongfly/mpv-winbuild release."
    }

    $tempDir = Join-Path ([IO.Path]::GetTempPath()) ("libmpv-dl-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    try {
        $archivePath = Join-Path $tempDir $asset.name
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archivePath

        $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
        if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) {
            $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe"
        }
        if (-not $sevenZip) {
            throw "7z.exe is required to extract $($asset.name). Install 7-Zip or place libmpv-2.dll at $libMpvTarget manually."
        }

        $extractDir = Join-Path $tempDir "extracted"
        New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
        & $sevenZip x $archivePath "-o$extractDir" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to extract $($asset.name)." }

        $dll = Get-ChildItem -Path $extractDir -Recurse -Filter "libmpv-2.dll" | Select-Object -First 1
        if (-not $dll) { throw "libmpv-2.dll was not found inside $($asset.name)." }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $libMpvTarget) | Out-Null
        Copy-Item $dll.FullName $libMpvTarget -Force
        Write-Host ("Downloaded libmpv-2.dll ({0:N2} MB) to {1}" -f ((Get-Item $libMpvTarget).Length / 1MB), $libMpvTarget) -ForegroundColor Green
    }
    finally {
        if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if (-not $SkipPublish) {
    if (Test-Path $deployDir) {
        Remove-Item $deployDir -Recurse -Force
    }

    Write-Host "Publishing CDBurner10.Upscaler (win-x64) to $deployDir..." -ForegroundColor Cyan
    & $dotnet publish (Join-Path $repoRoot "src\CDBurner10.Upscaler\CDBurner10.Upscaler.csproj") -c Release -p:Platform=x64 -o $deployDir
    if ($LASTEXITCODE -ne 0) { throw "CDBurner10.Upscaler publish failed." }
}

$exePath = Join-Path $deployDir "CDBurner10.Upscaler.exe"
if (-not (Test-Path $exePath)) {
    throw "Expected executable not found at $exePath"
}

$deployedMpv = Join-Path $deployDir "libmpv-2.dll"
if (-not (Test-Path $deployedMpv)) {
    Write-Warning "libmpv-2.dll is not present in $deployDir. GLSL shader presets will require installing libmpv-2.dll manually."
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $msiPath) { Remove-Item $msiPath -Force }

Write-Host "Building 64-bit Upscaler MSI installer with WiX..." -ForegroundColor Cyan
& wix build $wxsPath `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d "ProductVersion=$Version" `
    -d "DeployDir=$deployDir" `
    -d "AppIconPath=$iconPath" `
    -d "LicensePath=$licensePath" `
    -o $msiPath

if ($LASTEXITCODE -ne 0) { throw "WiX build failed." }

$fi = Get-Item $msiPath
Write-Host ("Built MSI: {0} ({1:N2} MB)" -f $fi.FullName, ($fi.Length / 1MB)) -ForegroundColor Green
