param(
    [string]$Version = "10.6.0.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$deployDir = Join-Path $repoRoot "deploy"
$outDir = Join-Path $PSScriptRoot "out"
$wxsPath = Join-Path $PSScriptRoot "Package.wxs"
$iconPath = Join-Path $repoRoot "src\CDBurner10.App\Assets\app.ico"
$licensePath = Join-Path $PSScriptRoot "License.rtf"
$shortVer = ($Version -split '\.')[0..2] -join '.'
$msiPath = Join-Path $outDir "CDBurner10-$shortVer-win-x64.msi"

$dotnet = "C:\Users\Monika\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

if (-not $SkipPublish) {
    Write-Host "Publishing CDBurner10.App and CDBurner10.Cli (win-x64) to $deployDir..." -ForegroundColor Cyan
    & $dotnet publish (Join-Path $repoRoot "src\CDBurner10.App\CDBurner10.App.csproj") -c Release -p:Platform=x64 -o $deployDir
    if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

    & $dotnet publish (Join-Path $repoRoot "src\CDBurner10.Cli\CDBurner10.Cli.csproj") -c Release -p:Platform=x64 -o $deployDir
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $msiPath) { Remove-Item $msiPath -Force }

Write-Host "Building 64-bit MSI installer with WiX..." -ForegroundColor Cyan
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
