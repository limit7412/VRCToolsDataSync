<#
.SYNOPSIS
  VRCToolsDataSync の App と Cli をリリース構成で publish して artifacts/ にまとめる。

.PARAMETER Arch
  ターゲットアーキテクチャ。x64 (既定) / arm64 / x86 のいずれか。

.PARAMETER Configuration
  ビルド構成。既定は Release。

.PARAMETER OutputDir
  出力先のルート。既定は <repo>/artifacts。

.EXAMPLE
  pwsh scripts/build-release.ps1
  pwsh scripts/build-release.ps1 -Arch arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('x64','arm64','x86')]
    [string]$Arch = 'x64',

    [string]$Configuration = 'Release',

    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'artifacts'
}

$rid = "win-$Arch"
$appProject = Join-Path $repoRoot 'src/VRCToolsDataSync.App/VRCToolsDataSync.App.csproj'
$cliProject = Join-Path $repoRoot 'src/VRCToolsDataSync.Cli/VRCToolsDataSync.Cli.csproj'

$archStagingDir = Join-Path $OutputDir $rid
$appStagingDir = Join-Path $archStagingDir 'app'
$cliStagingDir = Join-Path $archStagingDir 'cli'

Write-Host "[1/4] Cleaning staging directory: $archStagingDir"
if (Test-Path $archStagingDir) {
    Remove-Item $archStagingDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appStagingDir | Out-Null
New-Item -ItemType Directory -Force -Path $cliStagingDir | Out-Null

Write-Host "[2/4] Publishing App ($rid)"
& dotnet publish $appProject `
    -c $Configuration `
    -r $rid `
    --self-contained true `
    -p:Platform=$Arch `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -p:PublishSingleFile=false `
    -p:WindowsAppSDKSelfContained=true `
    -o $appStagingDir
if ($LASTEXITCODE -ne 0) { throw "App publish failed (exit $LASTEXITCODE)" }

Write-Host "[3/4] Publishing Cli ($rid)"
& dotnet publish $cliProject `
    -c $Configuration `
    -r $rid `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -p:PublishSingleFile=false `
    -o $cliStagingDir
if ($LASTEXITCODE -ne 0) { throw "Cli publish failed (exit $LASTEXITCODE)" }

Write-Host "[4/4] Creating zip archive"
$zipPath = Join-Path $OutputDir "VRCToolsDataSync-$rid.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$archStagingDir/*" -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Done."
Write-Host "  App: $appStagingDir"
Write-Host "  Cli: $cliStagingDir"
Write-Host "  Zip: $zipPath"
