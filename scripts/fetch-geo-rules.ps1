#Requires -Version 5.1
<#
  sing-box rule-sets для VPN split (geosite/geoip РФ).
#>
param(
    [string]$OutDir = "",
    [string]$CacheDir = ""
)

$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $ProjectDir "tools" }
if (-not $CacheDir) { $CacheDir = Join-Path $ProjectDir ".cache\geo-rules" }

New-Item -ItemType Directory -Force -Path $OutDir, $CacheDir | Out-Null

$files = @(
    @{ Name = "geoip-ru.srs"; Url = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs" },
    @{ Name = "geosite-category-ru.srs"; Url = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-category-ru.srs" },
    @{ Name = "geosite-category-gov-ru.srs"; Url = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-category-gov-ru.srs" }
)

foreach ($f in $files) {
    $dest = Join-Path $OutDir $f.Name
    if ((Test-Path $dest) -and ((Get-Item $dest).Length -gt 32)) {
        Write-Host "    $($f.Name) уже есть ✓"
        continue
    }
    $cache = Join-Path $CacheDir $f.Name
    if (-not (Test-Path $cache)) {
        Write-Host "    Скачивание $($f.Name)..."
        Invoke-WebRequest -Uri $f.Url -OutFile $cache -UseBasicParsing
    }
    Copy-Item $cache $dest -Force
    Write-Host "    $($f.Name) ✓"
}
