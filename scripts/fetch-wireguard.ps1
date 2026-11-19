#Requires -Version 5.1
<#
  Скачивает wireguard.exe + wintun.dll для bundling в установщик.
  Официальные источники: download.wireguard.com + wintun.net
#>
param(
    [string]$OutDir = "",
    [string]$CacheDir = ""
)

$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $ProjectDir "tools" }
if (-not $CacheDir) { $CacheDir = Join-Path $ProjectDir ".cache\wireguard" }

New-Item -ItemType Directory -Force -Path $OutDir, $CacheDir | Out-Null

$wgMsiVer = "0.5.3"
$wintunVer = "0.14.1"
$wgMsi = Join-Path $CacheDir "wireguard-amd64-$wgMsiVer.msi"
$wintunZip = Join-Path $CacheDir "wintun-$wintunVer.zip"
$wgExe = Join-Path $OutDir "wireguard.exe"
$wintunDll = Join-Path $OutDir "wintun.dll"

if ((Test-Path $wgExe) -and (Test-Path $wintunDll)) {
    Write-Host "    WireGuard tools уже есть ✓"
    return
}

Write-Host "    Скачивание WireGuard $wgMsiVer + Wintun $wintunVer..."

# ── wintun.dll ────────────────────────────────────────────────────────────────
if (-not (Test-Path $wintunZip)) {
    $wintunUrl = "https://www.wintun.net/builds/wintun-$wintunVer.zip"
    Invoke-WebRequest -Uri $wintunUrl -OutFile $wintunZip -UseBasicParsing
}
$wintunExtract = Join-Path $CacheDir "wintun-extract"
if (Test-Path $wintunExtract) { Remove-Item $wintunExtract -Recurse -Force }
Expand-Archive -Path $wintunZip -DestinationPath $wintunExtract -Force
$dllSrc = Get-ChildItem -Path $wintunExtract -Recurse -Filter "wintun.dll" |
    Where-Object { $_.FullName -match "amd64" } | Select-Object -First 1
if (-not $dllSrc) { throw "wintun.dll (amd64) не найден в архиве" }
Copy-Item $dllSrc.FullName $wintunDll -Force

# ── wireguard.exe из официального MSI ─────────────────────────────────────────
if (-not (Test-Path $wgMsi)) {
    $msiUrl = "https://download.wireguard.com/windows-client/wireguard-amd64-$wgMsiVer.msi"
    Invoke-WebRequest -Uri $msiUrl -OutFile $wgMsi -UseBasicParsing
}

$msiExtract = Join-Path $CacheDir "msi-extract"
if (Test-Path $msiExtract) { Remove-Item $msiExtract -Recurse -Force }
New-Item -ItemType Directory -Force -Path $msiExtract | Out-Null

$proc = Start-Process -FilePath "msiexec.exe" -ArgumentList @(
    "/a", "`"$wgMsi`"", "/qn",
    "TARGETDIR=`"$msiExtract`""
) -Wait -PassThru -NoNewWindow

if ($proc.ExitCode -ne 0) { throw "msiexec extract failed: $($proc.ExitCode)" }

$exeSrc = Get-ChildItem -Path $msiExtract -Recurse -Filter "wireguard.exe" | Select-Object -First 1
if (-not $exeSrc) { throw "wireguard.exe не найден в MSI" }
Copy-Item $exeSrc.FullName $wgExe -Force

Write-Host "    wireguard.exe + wintun.dll ✓ → $OutDir"
