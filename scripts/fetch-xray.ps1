#Requires -Version 5.1
<#
  Скачивает xray-core (windows-amd64) для VPN TUN.
  Официальный релиз: https://github.com/XTLS/Xray-core/releases
#>
param(
    [string]$OutDir = "",
    [string]$CacheDir = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $ProjectDir "tools" }
if (-not $CacheDir) { $CacheDir = Join-Path $ProjectDir ".cache\xray" }

New-Item -ItemType Directory -Force -Path $OutDir, $CacheDir | Out-Null

$xrayExe = Join-Path $OutDir "xray.exe"
if (Test-Path $xrayExe) {
    Write-Host "    xray.exe уже есть ✓"
    return
}

$zipName = "Xray-windows-64.zip"
$zipPath = Join-Path $CacheDir $zipName

function Get-LatestXrayAsset {
    $api = Invoke-RestMethod -Uri "https://api.github.com/repos/XTLS/Xray-core/releases/latest" -UseBasicParsing
    $asset = $api.assets | Where-Object { $_.name -eq $zipName } | Select-Object -First 1
    if (-not $asset) { throw "Xray-windows-64.zip не найден в latest release ($($api.tag_name))" }
    return @{
        Url = $asset.browser_download_url
        Tag = $api.tag_name
    }
}

if ($Version) {
    $tag = if ($Version -match '^v') { $Version } else { "v$Version" }
    $url = "https://github.com/XTLS/Xray-core/releases/download/$tag/$zipName"
    Write-Host "    Скачивание Xray-core $tag..."
}
else {
    $latest = Get-LatestXrayAsset
    $url = $latest.Url
    Write-Host "    Скачивание Xray-core $($latest.Tag) (latest)..."
}

if (-not (Test-Path $zipPath)) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
    }
    catch {
        if ($Version) {
            Write-Host "    Релиз недоступен, пробуем latest..."
            Remove-Item $zipPath -ErrorAction SilentlyContinue
            $latest = Get-LatestXrayAsset
            Invoke-WebRequest -Uri $latest.Url -OutFile $zipPath -UseBasicParsing
        }
        else {
            throw
        }
    }
}

$extractDir = Join-Path $CacheDir "extract"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

$exeSrc = Get-ChildItem -Path $extractDir -Recurse -Filter "xray.exe" | Select-Object -First 1
if (-not $exeSrc) { throw "xray.exe не найден в архиве" }
Copy-Item $exeSrc.FullName $xrayExe -Force

foreach ($geo in @("geosite.dat", "geoip.dat")) {
    $geoSrc = Get-ChildItem -Path $extractDir -Recurse -Filter $geo | Select-Object -First 1
    if ($geoSrc) {
        Copy-Item $geoSrc.FullName (Join-Path $OutDir $geo) -Force
        Write-Host "    $geo из Xray-core ✓"
    }
}

$wintunSrc = Get-ChildItem -Path $extractDir -Recurse -Filter "wintun.dll" | Select-Object -First 1
if ($wintunSrc) {
    Copy-Item $wintunSrc.FullName (Join-Path $OutDir "wintun.dll") -Force
    Write-Host "    wintun.dll из Xray-core ✓"
}

Write-Host "    xray.exe ✓ → $OutDir"
