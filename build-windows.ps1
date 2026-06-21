#Requires -Version 5.1
<#
.SYNOPSIS
  Сборка ByPassMe для Windows → установщик ByPassMe-Setup.exe

.USAGE
  .\build-windows.ps1
  .\build-windows.ps1 -CreateInstaller
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$HubMosToken = $env:HUB_MOS_TOKEN,
    [switch]$CreateInstaller
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectDir

$AppVersion = "1.1.33"
Write-Host "📂 Проект: $ProjectDir (v$AppVersion)"

# ── 0. hub.mos.ru token ───────────────────────────────────────────────────────
if (-not $HubMosToken -and (Test-Path "$ProjectDir\Secrets.props")) {
    $content = Get-Content "$ProjectDir\Secrets.props" -Raw
    if ($content -match '<HUB_MOS_TOKEN>([^<]+)</HUB_MOS_TOKEN>') {
        $HubMosToken = $Matches[1].Trim()
    }
}

if (-not $HubMosToken -or $HubMosToken -eq "your_hub_mos_token_here") {
    Write-Warning "⚠️  HUB_MOS_TOKEN не задан — список серверов будет только из кэша"
    $HubMosToken = "@HUB_MOS_TOKEN@"
}

$hubTokenPath = "$ProjectDir\ByPassMe\HubToken.cs"
$escaped = $HubMosToken -replace '\\', '\\\\' -replace '"', '\"'
@"
namespace ByPassMe;

internal static class HubToken
{
    internal const string Mos = "$escaped";
}
"@ | Set-Content -Path $hubTokenPath -Encoding UTF8
Write-Host "🔑 HubToken.cs обновлён"

# ── 1. Go client ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "⚙️  [1/6] Сборка bypassclient.exe..."
$goDir = "$ProjectDir\go_client"
$goOut = "$goDir\bypassclient.exe"

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    Write-Error "Go не установлен: https://go.dev/dl/"
}

Push-Location $goDir
try {
    $env:GOOS = "windows"
    $env:GOARCH = "amd64"
    $env:CGO_ENABLED = "0"
    if (-not $env:GOTOOLCHAIN) { $env:GOTOOLCHAIN = "auto" }
    go build -ldflags="-s -w" -o $goOut .
    if (-not (Test-Path $goOut)) { throw "bypassclient.exe не создан" }
    Write-Host "    bypassclient.exe ✓"
}
finally {
    Pop-Location
}

# ── 2. .NET build ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "🔨  [2/6] dotnet build ($Configuration)..."
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET 8 SDK не установлен: https://dotnet.microsoft.com/download/dotnet/8.0"
}

dotnet restore "$ProjectDir\ByPassMe.sln"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build "$ProjectDir\ByPassMe.sln" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ── 3. Publish (self-contained — .NET 8 встроен, ничего ставить отдельно) ─────
Write-Host ""
Write-Host "📦  [3/6] dotnet publish (self-contained)..."
$publishDir = "$ProjectDir\publish\$Configuration"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish "$ProjectDir\ByPassMe\ByPassMe.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item $goOut "$publishDir\bypassclient.exe" -Force

# ── 4. TunnelHelper (фоновая служба VPN, без UAC) ────────────────────────────
Write-Host ""
Write-Host "🔧  [4/6] ByPassMe.TunnelHelper → publish\tools..."
New-Item -ItemType Directory -Force -Path "$publishDir\tools" | Out-Null

dotnet publish "$ProjectDir\ByPassMe.TunnelHelper\ByPassMe.TunnelHelper.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o "$publishDir\tools"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "    ByPassMe.TunnelHelper.exe ✓"

# ── 5. WireGuard (bundled в установщик) ───────────────────────────────────────
Write-Host ""
Write-Host "📥  [5/6] WireGuard + Wintun → publish\tools..."
& "$ProjectDir\scripts\fetch-wireguard.ps1" -OutDir "$publishDir\tools"

# ── 6. Установщик Inno Setup ──────────────────────────────────────────────────
$setupExe = "$ProjectDir\dist\ByPassMe-Setup-$AppVersion.exe"

if ($CreateInstaller) {
    Write-Host ""
    Write-Host "🧰  [6/6] Inno Setup → ByPassMe-Setup.exe..."

    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Host "    Inno Setup не найден — устанавливаю через winget..."
        winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements --silent 2>$null
        $iscc = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if (-not $iscc) {
        Write-Error "Inno Setup 6 не установлен. Установите: https://jrsoftware.org/isinfo.php"
    }

    New-Item -ItemType Directory -Force -Path "$ProjectDir\dist" | Out-Null

    $publishDirIss = ($publishDir -replace '\\', '/')

    & $iscc `
        "/DAppVersion=$AppVersion" `
        "/DPublishDir=$publishDirIss" `
        "$ProjectDir\installer\ByPassMe.iss"

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not (Test-Path $setupExe)) {
        Write-Error "Установщик не создан: $setupExe"
    }

    $sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Host "    ByPassMe-Setup-$AppVersion.exe ✓ ($sizeMb MB)"
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗"
if ($CreateInstaller -and (Test-Path $setupExe)) {
    Write-Host "║  ✅  Установщик: $setupExe"
} else {
    Write-Host "║  ✅  Сборка: $publishDir"
    Write-Host "║  💡  Добавьте -CreateInstaller для .exe установщика"
}
Write-Host "╚══════════════════════════════════════════════════════════╝"
