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

$AppVersion = "1.2.5"
Write-Host "📂 Проект: $ProjectDir (v$AppVersion)"

# ── 0. hub.mos.ru token (XOR, как Android — без plaintext glpat в бинарнике/исходниках) ─
if (-not $HubMosToken -and (Test-Path "$ProjectDir\Secrets.props")) {
    $content = Get-Content "$ProjectDir\Secrets.props" -Raw
    if ($content -match '<HUB_MOS_TOKEN>([^<]+)</HUB_MOS_TOKEN>') {
        $HubMosToken = $Matches[1].Trim()
    }
}

if (-not $HubMosToken -or $HubMosToken -eq "your_hub_mos_token_here" -or $HubMosToken -eq "@HUB_MOS_TOKEN@") {
    Write-Warning "⚠️  HUB_MOS_TOKEN не задан — список серверов будет только из кэша"
    $HubMosToken = ""
}

$hubTokenPath = "$ProjectDir\ByPassMe\HubToken.cs"
$key = 0x5C
$utf8 = [System.Text.Encoding]::UTF8.GetBytes($HubMosToken)
$encParts = for ($i = 0; $i -lt $utf8.Length; $i++) {
    $b = $utf8[$i] -bxor $key -bxor ($i -band 0xFF)
    "0x{0:X2}" -f $b
}
$encCsv = if ($encParts) { ($encParts -join ", ") } else { "" }

@"
namespace ByPassMe;

/// <summary>
/// Токен hub.mos.ru — XOR-байты (как Android HubToken.kt).
/// Plaintext glpat не хранится в исходниках; Enc подставляется при сборке.
/// </summary>
internal static class HubToken
{
    private const byte Key = 0x5C;

    private static readonly byte[] Enc = { $encCsv };

    private static string? _cached;

    internal static string Mos
    {
        get
        {
            if (_cached != null) return _cached;
            _cached = Decode(Enc);
            return _cached;
        }
    }

    private static string Decode(byte[] enc)
    {
        if (enc.Length == 0) return "";
        var bytes = new byte[enc.Length];
        for (var i = 0; i < enc.Length; i++)
            bytes[i] = (byte)(enc[i] ^ Key ^ (i & 0xFF));
        try { return System.Text.Encoding.UTF8.GetString(bytes); }
        catch { return ""; }
    }
}
"@ | Set-Content -Path $hubTokenPath -Encoding UTF8
Write-Host "🔑 HubToken.cs обновлён (XOR, $($utf8.Length) байт)"


# ── 1. Go client ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "⚙️  [1/7] Сборка bypassclient.exe..."
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
Write-Host "🔨  [2/7] dotnet build ($Configuration)..."
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET 8 SDK не установлен: https://dotnet.microsoft.com/download/dotnet/8.0"
}

dotnet restore "$ProjectDir\ByPassMe.sln"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build "$ProjectDir\ByPassMe.sln" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ── 3. Publish (self-contained — .NET 8 встроен, ничего ставить отдельно) ─────
Write-Host ""
Write-Host "📦  [3/7] dotnet publish (self-contained)..."
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
Write-Host "🔧  [4/7] ByPassMe.TunnelHelper → publish\tools..."
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
Write-Host "📥  [5/7] WireGuard + Wintun → publish\tools..."
& "$ProjectDir\scripts\fetch-wireguard.ps1" -OutDir "$publishDir\tools"

# Обход поднимает адаптер сам (режим rawtun): wintun.dll грузится только из папки
# самой программы (LOAD_LIBRARY_SEARCH_APPLICATION_DIR), поэтому кладём его рядом
# с bypassclient.exe, а не только в tools.
Copy-Item "$publishDir\tools\wintun.dll" "$publishDir\wintun.dll" -Force -ErrorAction SilentlyContinue
if (-not (Test-Path "$publishDir\wintun.dll")) { throw "wintun.dll не найден — обход не поднимет туннель" }
Write-Host "    wintun.dll рядом с bypassclient.exe ✓"

# ── 6. Xray-core + sing-box (VPN: xray SOCKS + sing-box TUN) ──────────────────
Write-Host ""
Write-Host "📥  [6/7] Xray-core + sing-box → publish\tools..."
& "$ProjectDir\scripts\fetch-xray.ps1" -OutDir "$publishDir\tools"
& "$ProjectDir\scripts\fetch-singbox.ps1" -OutDir "$publishDir\tools"
& "$ProjectDir\scripts\fetch-geo-rules.ps1" -OutDir "$publishDir\tools"

Write-Host ""
Write-Host "🧪  Проверка маршрутизации (sing-box parity)..."
Copy-Item "$publishDir\tools\*.srs" "$ProjectDir\.cache\" -Force -ErrorAction SilentlyContinue
python3 "$ProjectDir\scripts\generate-test-configs.py"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python3 "$ProjectDir\scripts\validate-routing-configs.py"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "    validate-routing-configs ✓"

# ── 7. Установщик Inno Setup ──────────────────────────────────────────────────
$setupExe = "$ProjectDir\dist\ByPassMe-Setup-$AppVersion.exe"

if ($CreateInstaller) {
    Write-Host ""
    Write-Host "🧰  [7/7] Inno Setup → ByPassMe-Setup.exe..."

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
