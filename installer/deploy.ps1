$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$deployDir = Join-Path $repoRoot "deploy"

Write-Host "Publishing CDBurner10.App and CDBurner10.Cli (win-x64) to $deployDir..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repoRoot "src\CDBurner10.App\CDBurner10.App.csproj") -c Release -p:Platform=x64 -o $deployDir
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

& dotnet publish (Join-Path $repoRoot "src\CDBurner10.Cli\CDBurner10.Cli.csproj") -c Release -p:Platform=x64 -o $deployDir
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }