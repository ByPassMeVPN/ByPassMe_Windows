#Requires -Version 5.1
<#
  Скачивает sing-box (windows-amd64) для VPN TUN (схема v2rayN: TUN → SOCKS xray).
  Официальный релиз: https://github.com/SagerNet/sing-box/releases
#>
param(
    [string]$OutDir = "",
    [string]$CacheDir = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $ProjectDir "tools" }
if (-not $CacheDir) { $CacheDir = Join-Path $ProjectDir ".cache\sing-box" }

New-Item -ItemType Directory -Force -Path $OutDir, $CacheDir | Out-Null

$singboxExe = Join-Path $OutDir "sing-box.exe"
if (Test-Path $singboxExe) {
    Write-Host "    sing-box.exe уже есть ✓"
    return
}

function Get-LatestSingboxAsset {
    $api = Invoke-RestMethod -Uri "https://api.github.com/repos/SagerNet/sing-box/releases/latest" -UseBasicParsing
    $asset = $api.assets | Where-Object {
        $_.name -like "sing-box-*-windows-amd64.zip" -and $_.name -notlike "*legacy*"
    } | Select-Object -First 1
    if (-not $asset) { throw "sing-box-*-windows-amd64.zip не найден в latest ($($api.tag_name))" }
    return @{
        Url = $asset.browser_download_url
        Tag = $api.tag_name
        Name = $asset.name
    }
}

if ($Version) {
    $tag = if ($Version -match '^v') { $Version } else { "v$Version" }
    $ver = $tag.TrimStart('v')
    $zipName = "sing-box-$ver-windows-amd64.zip"
    $url = "https://github.com/SagerNet/sing-box/releases/download/$tag/$zipName"
    Write-Host "    Скачивание sing-box $tag..."
}
else {
    $latest = Get-LatestSingboxAsset
    $url = $latest.Url
    $zipName = $latest.Name
    Write-Host "    Скачивание sing-box $($latest.Tag) (latest)..."
}

$zipPath = Join-Path $CacheDir $zipName

if (-not (Test-Path $zipPath)) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
    }
    catch {
        if ($Version) {
            Write-Host "    Релиз недоступен, пробуем latest..."
            Remove-Item $zipPath -ErrorAction SilentlyContinue
            $latest = Get-LatestSingboxAsset
            Invoke-WebRequest -Uri $latest.Url -OutFile (Join-Path $CacheDir $latest.Name) -UseBasicParsing
            $zipPath = Join-Path $CacheDir $latest.Name
        }
        else {
            throw
        }
    }
}

$extractDir = Join-Path $CacheDir "extract"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

$exeSrc = Get-ChildItem -Path $extractDir -Recurse -Filter "sing-box.exe" | Select-Object -First 1
if (-not $exeSrc) { throw "sing-box.exe не найден в архиве" }
Copy-Item $exeSrc.FullName $singboxExe -Force

Write-Host "    sing-box.exe ✓ → $OutDir"
