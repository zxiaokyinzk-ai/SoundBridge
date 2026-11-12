$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet restore AudioRouter.csproj --packages .nuget/packages
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet publish AudioRouter.csproj --no-restore -c Release '-p:PublishDir=dist\声桥\'
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Please close Sound Bridge and try again.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $PSScriptRoot 'dist\声桥\使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $PSScriptRoot 'dist\声桥\THIRD-PARTY-NOTICES.txt') -Force
Write-Host 'Ready: dist/声桥/声桥.exe'
