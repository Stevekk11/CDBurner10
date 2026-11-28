$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$deployDir = Join-Path $repoRoot "deploy-upscaler"

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

if (Test-Path $deployDir) {
    Remove-Item $deployDir -Recurse -Force
}

Write-Host "Publishing CDBurner10.Upscaler (win-x64) to $deployDir..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $repoRoot "src\CDBurner10.Upscaler\CDBurner10.Upscaler.csproj") -c Release -p:Platform=x64 -o $deployDir
if ($LASTEXITCODE -ne 0) { throw "CDBurner10.Upscaler publish failed." }

if (-not (Test-Path (Join-Path $deployDir "libmpv-2.dll"))) {
    Write-Warning "libmpv-2.dll was not copied to $deployDir. Place it in native\upscaler\libmpv-2.dll or run installer\build-upscaler-msi.ps1 -DownloadLibMpv."
}