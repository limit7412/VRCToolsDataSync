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

$stagingRoot = Join-Path $OutputDir $rid
$appStagingDir = Join-Path $stagingRoot 'app'
$cliStagingDir = Join-Path $stagingRoot 'cli'
$shortcutPath   = Join-Path $stagingRoot 'VRCToolsDataSync.lnk'
$shortcutTarget = Join-Path $appStagingDir 'VRCToolsDataSync.App.exe'

Write-Host "[1/6] Cleaning staging directory: $stagingRoot"
if (Test-Path $stagingRoot) {
    Remove-Item $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appStagingDir | Out-Null
New-Item -ItemType Directory -Force -Path $cliStagingDir | Out-Null

# NOTE: 後段で & dotnet publish を経た直後の PowerShell では
# Join-Path / 文字列補間 / WScript.Shell の組み合わせが安定せず、
# 変数値が空文字に解決されたり Save() が無音失敗する事象を観測した。
# このため .lnk は dotnet publish 前にこの位置で生成しておく。
# ターゲットの exe はまだ存在しないが、ショートカットの解決は
# 起動時にリンク追跡で行われるため事前生成で問題ない。
Write-Host "[2/6] Creating launcher shortcut: $shortcutPath"
$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $shortcutTarget
$shortcut.WorkingDirectory = $appStagingDir
$shortcut.IconLocation = "$shortcutTarget,0"
$shortcut.Description = 'VRCToolsDataSync'
$shortcut.Save()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($wshShell) | Out-Null
[GC]::Collect()
[GC]::WaitForPendingFinalizers()
if (-not [System.IO.File]::Exists($shortcutPath)) {
    throw "Shortcut was not produced at: $shortcutPath"
}

Write-Host "[3/6] Publishing App ($rid)"
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

Write-Host "[4/6] Publishing Cli ($rid)"
& dotnet publish $cliProject `
    -c $Configuration `
    -r $rid `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -p:PublishSingleFile=false `
    -o $cliStagingDir
if ($LASTEXITCODE -ne 0) { throw "Cli publish failed (exit $LASTEXITCODE)" }

Write-Host "[5/6] Verifying launcher shortcut and exe presence"
if (-not [System.IO.File]::Exists($shortcutTarget)) {
    throw "Shortcut target not found after publish: $shortcutTarget"
}
if (-not [System.IO.File]::Exists($shortcutPath)) {
    throw "Shortcut missing after publish: $shortcutPath"
}

Write-Host "[6/6] Creating zip archive"
$zipPath = Join-Path $OutputDir "VRCToolsDataSync-$rid.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
# Compress-Archive は PowerShell 5.1 で大きいフォルダに対して
# IndexOutOfRangeException を出すバグがあるため、.NET の ZipFile を直接使う。
# 5.1 と 7+ の両方で動かすには System.IO.Compression 本体も含めて
# Add-Type で明示的にロードする必要がある (CompressionLevel 型の解決のため)。
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stagingRoot,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host ""
Write-Host "Done."
Write-Host "  App: $appStagingDir"
Write-Host "  Cli: $cliStagingDir"
Write-Host "  Zip: $zipPath"
