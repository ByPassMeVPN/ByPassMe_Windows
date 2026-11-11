using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ByPassMe.Helpers;
using ByPassMe.Models;

namespace ByPassMe.Services;

/// <summary>
/// VPN как v2rayN Legacy Protect и Android:
/// xray (SOCKS 10808) → sing-box TUN (wintun) → весь трафик в SOCKS.
/// </summary>
public sealed class VpnManager
{
    public static VpnManager Instance { get; } = new();

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private const int ConnectTimeoutSeconds = 30;
    private const int WatchdogIntervalSeconds = 30;
    private const int MaxSoftRestartsPerWindow = 3;
    private static readonly TimeSpan SoftRestartWindow = TimeSpan.FromMinutes(5);

    private Process? _xray;
    private Process? _singbox;
    private CancellationTokenSource? _timeoutCts;
    private CancellationTokenSource? _watchdogCts;
    private Task? _cooldownTask;
    private Task? _watchdogTask;
    private string? _activeServerId;
    private VpnServer? _connectedServer;
    private string? _connectedUuid;
    private string? _connectIp;
    private readonly StringBuilder _coreOutput = new();
    private NetworkRouteHelper.WanLink? _wan;
    private readonly List<IPAddress> _pinnedRoutes = [];
    private string? _xrayPath;
    private string? _singboxPath;
    private string? _xrayConfigPath;
    private string? _singboxConfigPath;
    private bool _stopping;
    private bool _softRestarting;
    private int _healthFailCount;
    private int _softRestartCount;
    private DateTime _softRestartWindowStart = DateTime.MinValue;
    private bool _networkHooked;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private bool _hasNextReload;
    private VpnServer? _pendingReloadServer;

    private string _stage = "";

    public bool IsRunning { get; private set; }
    public bool IsReady { get; private set; }
    public bool IsConnecting { get; private set; }
    public int CooldownSeconds { get; private set; }
    public string? LastError { get; private set; }

    public event Action? Changed;

    private void Notify() => Changed?.Invoke();

    private void Stage(string name)
    {
        _stage = name;
        AppLogger.Instance.Vpn($"этап: {name}");
    }

    public async Task StartAsync(VpnServer server, CancellationToken ct = default)
    {
        if (IsRunning || IsConnecting) return;

        var uuid = SettingsStore.Instance.VpnUuid.Trim();
        if (string.IsNullOrEmpty(uuid))
        {
            LastError = "Нет UUID подписки — введите ссылку (🔑)";
            AppLogger.Instance.VpnErr(LastError);
            Notify();
            return;
        }

        var bypassWasRunning = BypassManager.Instance.IsRunning;
        StopBypassIfActive();
        if (bypassWasRunning)
            await Task.Delay(400, ct);

        IsConnecting = true;
        IsReady = false;
        IsRunning = true;
        LastError = null;
        _stopping = false;
        _activeServerId = server.Id;
        Notify();

        _xrayPath = FindTool("xray.exe");
        _singboxPath = FindTool("sing-box.exe");
        if (_xrayPath == null)
        {
            FailStart("xray.exe не найден. Переустановите ByPassMe.");
            return;
        }

        if (_singboxPath == null)
        {
            FailStart("sing-box.exe не найден. Переустановите ByPassMe.");
            return;
        }

        try
        {
            Stage("остановка старого туннеля");
            StopProcesses();
            await Task.Delay(100, ct);
            NetworkRouteHelper.RemoveTunAdapter();
            await Task.Delay(100, ct);

            Stage("поиск сетевой карты");
            _wan = NetworkRouteHelper.DetectWan();
            if (_wan == null)
            {
                FailStart("Нет активного сетевого подключения");
                return;
            }

            var serverIps = NetworkRouteHelper.ResolveIpv4(server.Address);
            PinControlRoutes(serverIps);
            var connectIp = serverIps.FirstOrDefault()?.ToString();
            _connectIp = connectIp;
            _connectedServer = server;
            _connectedUuid = uuid;
            AppLogger.Instance.Vpn($"WAN {_wan.Name} {_wan.Ipv4} gw={_wan.Gateway}");
            if (connectIp != null)
                AppLogger.Instance.Vpn($"IP узла {server.Address} → {connectIp}");
            else
                AppLogger.Instance.Vpn($"IP узла {server.Address} не резолвится — xray возьмёт имя");

            Stage("запись конфига xray");
            await WriteXrayConfigAsync(server, uuid, connectIp, ct);
            AppLogger.Instance.Vpn($"Подключение VPN: {server.Name} → {server.Address}:{server.Port}");

            Stage("запуск xray");
            AppLogger.Instance.VpnDbg($"xray: {_xrayPath}");
            if (!StartXray())
                return;

            Stage("ожидание SOCKS 127.0.0.1:10808");
            if (!await WaitForSocksAsync(TimeSpan.FromSeconds(5), ct))
            {
                FailStart(ReadFailure("xray не открыл SOCKS"));
                return;
            }
            AppLogger.Instance.Vpn("SOCKS готов");

            Stage("запись конфига sing-box");
            await WriteSingboxConfigAsync(server, ct);

            Stage("запуск sing-box TUN");
            AppLogger.Instance.VpnDbg($"sing-box: {_singboxPath}");
            if (!StartSingbox())
                return;

            Stage("ожидание адаптера singbox_tun");
            ScheduleConnectTimeout();
            await WaitForTunAsync(TimeSpan.FromSeconds(ConnectTimeoutSeconds), ct);

            if (!IsReady)
            {
                await Task.Delay(200, ct);
                FailStart(ReadFailure(LastError ?? "адаптер singbox_tun не поднялся"));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.VpnDbg(ex.ToString());
            var where = string.IsNullOrEmpty(_stage) ? "подключение" : _stage;
            FailStart($"сбой ({where}): {ex.Message}");
        }
    }

    public void Stop(bool clearError = true)
    {
        _stopping = true;
        CancelConnectTimeout();
        StopWatchdog();
        UnhookNetworkChange();
        ConnectionStatsService.Vpn.Stop();
        IsConnecting = false;
        IsReady = false;
        var wasRunning = IsRunning;
        IsRunning = false;
        _activeServerId = null;
        _connectedServer = null;
        _connectedUuid = null;
        _connectIp = null;
        _healthFailCount = 0;
        _softRestartCount = 0;

        StopProcesses();
        NetworkRouteHelper.RemoveTunAdapter();
        UnpinControlRoutes();

        if (clearError)
            LastError = null;

        if (wasRunning)
        {
            AppLogger.Instance.Vpn("VPN остановлен");
            StartCooldown(2);
        }

        Notify();
    }

    /// <summary>
    /// Переключение сервера/протокола как v2rayN Reload/LoadCore:
    /// новый конфиг → CoreStop → 100ms → RemoveTun → 100ms → CoreStart → WaitSocks.
    /// Очередь: повторные клики не теряются (_hasNextReload).
    /// </summary>
    public async Task SwitchServerAsync(VpnServer server, CancellationToken ct = default)
    {
        if (!IsRunning && !IsConnecting) return;
        if (string.Equals(_activeServerId, server.Id, StringComparison.Ordinal) && IsReady)
            return;

        _pendingReloadServer = server;
        if (!await _reloadLock.WaitAsync(0, ct))
        {
            _hasNextReload = true;
            AppLogger.Instance.VpnDbg($"reload в очереди → {server.Name}");
            return;
        }

        try
        {
            do
            {
                _hasNextReload = false;
                var target = _pendingReloadServer ?? server;
                await ReloadCoreAsync(target, ct);
            } while (_hasNextReload && (IsRunning || IsConnecting));
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>v2rayN CoreManager.LoadCore — полный быстрый рестарт без cooldown UI.</summary>
    private async Task ReloadCoreAsync(VpnServer server, CancellationToken ct)
    {
        var uuid = SettingsStore.Instance.VpnUuid.Trim();
        if (string.IsNullOrEmpty(uuid))
        {
            FailStart("Нет UUID подписки — введите ссылку (🔑)");
            return;
        }

        if (_xrayPath == null) _xrayPath = FindTool("xray.exe");
        if (_singboxPath == null) _singboxPath = FindTool("sing-box.exe");
        if (_xrayPath == null || _singboxPath == null)
        {
            FailStart("xray/sing-box не найдены. Переустановите ByPassMe.");
            return;
        }

        CancelConnectTimeout();
        StopWatchdog();

        IsConnecting = true;
        IsReady = false;
        IsRunning = true;
        LastError = null;
        _stopping = false;
        _softRestarting = true;
        _activeServerId = server.Id;
        Notify();

        AppLogger.Instance.Vpn($"Переключение (v2rayN reload) → {server.Name}");

        try
        {
            if (_wan == null)
            {
                Stage("поиск сетевой карты");
                _wan = NetworkRouteHelper.DetectWan();
                if (_wan == null)
                {
                    FailStart("Нет активного сетевого подключения");
                    return;
                }
            }

            var serverIps = NetworkRouteHelper.ResolveIpv4(server.Address);
            PinControlRoutes(serverIps);
            var connectIp = serverIps.FirstOrDefault()?.ToString();
            _connectIp = connectIp;
            _connectedServer = server;
            _connectedUuid = uuid;
            if (connectIp != null)
                AppLogger.Instance.Vpn($"IP узла {server.Address} → {connectIp}");

            // Конфиг пишется до остановки — как GenerateClientConfig в v2rayN
            Stage("запись конфига xray");
            await WriteXrayConfigAsync(server, uuid, connectIp, ct);
            Stage("запись конфига sing-box");
            await WriteSingboxConfigAsync(server, ct);
            AppLogger.Instance.Vpn($"Подключение VPN: {server.Name} → {server.Address}:{server.Port}");

            Stage("остановка ядер");
            StopProcesses();
            await Task.Delay(100, ct);
            NetworkRouteHelper.RemoveTunAdapter();
            await Task.Delay(100, ct);

            Stage("запуск xray");
            if (!StartXray())
                return;

            Stage("ожидание SOCKS 127.0.0.1:10808");
            if (!await WaitForSocksAsync(TimeSpan.FromSeconds(5), ct))
            {
                FailStart(ReadFailure("xray не открыл SOCKS"));
                return;
            }
            AppLogger.Instance.Vpn("SOCKS готов");

            Stage("запуск sing-box TUN");
            if (!StartSingbox())
                return;

            Stage("ожидание адаптера singbox_tun");
            ScheduleConnectTimeout();
            await WaitForTunAsync(TimeSpan.FromSeconds(ConnectTimeoutSeconds), ct);

            if (!IsReady)
            {
                await Task.Delay(100, ct);
                FailStart(ReadFailure(LastError ?? "адаптер singbox_tun не поднялся"));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.VpnDbg(ex.ToString());
            FailStart($"сбой переключения: {ex.Message}");
        }
        finally
        {
            _softRestarting = false;
        }
    }

    private void MarkReady()
    {
        if (!IsRunning) return;
        CancelConnectTimeout();
        IsConnecting = false;
        IsReady = true;
        LastError = null;
        _healthFailCount = 0;
        _softRestartCount = 0;
        _softRestartWindowStart = DateTime.UtcNow;
        HookNetworkChange();
        StartWatchdog();
        ConnectionStatsService.Vpn.Start();
        AppLogger.Instance.Vpn($"VPN подключён ({_activeServerId})");
        Notify();
    }

    private void FailStart(string message)
    {
        _stopping = true;
        CancelConnectTimeout();
        ConnectionStatsService.Vpn.Stop();
        LastError = message;
        AppLogger.Instance.VpnErr(message);
        StopProcesses();
        NetworkRouteHelper.RemoveTunAdapter();
        UnpinControlRoutes();
        IsConnecting = false;
        IsReady = false;
        IsRunning = false;
        _activeServerId = null;
        StartCooldown(2);
        Notify();
    }

    private async Task WriteXrayConfigAsync(VpnServer server, string uuid, string? connectIp, CancellationToken ct)
    {
        var configDir = ConfigDir();
        Directory.CreateDirectory(configDir);
        _xrayConfigPath = Path.Combine(configDir, "xray-config.json");
        try { File.Delete(XrayConfigBuilder.ErrorLogPath); } catch { /* ignore */ }
        var json = XrayConfigBuilder.Build(server, uuid, _wan, connectIp);
        await File.WriteAllTextAsync(_xrayConfigPath, json, Utf8NoBom, ct);
    }

    private async Task WriteSingboxConfigAsync(VpnServer server, CancellationToken ct)
    {
        var configDir = ConfigDir();
        Directory.CreateDirectory(configDir);
        _singboxConfigPath = Path.Combine(configDir, "singbox-tun.json");

        var names = new[] { "xray.exe", "sing-box.exe", "ByPassMe.exe" };
        var paths = new List<string>();
        if (_xrayPath != null) paths.Add(Path.GetFullPath(_xrayPath));
        if (_singboxPath != null) paths.Add(Path.GetFullPath(_singboxPath));
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            paths.Add(Path.GetFullPath(Environment.ProcessPath));

        var protect = VpnProtectDomains.Collect(server);

        var ruleSetDir = Path.GetDirectoryName(Path.GetFullPath(_singboxPath!));
        var geoOk = RuSplitRouting.BundledRuleSets.Count(f =>
            File.Exists(Path.Combine(ruleSetDir ?? "", f.File)) && new FileInfo(Path.Combine(ruleSetDir!, f.File)).Length > 32);
        if (geoOk < RuSplitRouting.BundledRuleSets.Length)
            AppLogger.Instance.VpnErr($"geo rule-sets: {geoOk}/{RuSplitRouting.BundledRuleSets.Length} — без них vk.com/yandex.com пойдут в VPN");
        else
            AppLogger.Instance.VpnDbg($"geo rule-sets: {geoOk}/{RuSplitRouting.BundledRuleSets.Length} ✓");

        var geoDat = Path.Combine(ruleSetDir ?? "", "geosite.dat");
        var ipDat = Path.Combine(ruleSetDir ?? "", "geoip.dat");
        if (!File.Exists(geoDat) || !File.Exists(ipDat))
            AppLogger.Instance.VpnErr("geosite.dat/geoip.dat не найдены в tools — xray не распознает geosite:ru");

        var json = SingboxTunConfigBuilder.BuildVpn(
            XrayConfigBuilder.SocksPort, names, paths, protect, ruleSetDir);
        AppLogger.Instance.Vpn("маршрут VPN: все сайты РФ напрямую, остальные через туннель");
        await File.WriteAllTextAsync(_singboxConfigPath, json, Utf8NoBom, ct);
        AppLogger.Instance.VpnDbg($"sing-box конфиг: {_singboxConfigPath}");
    }

    private bool StartXray()
    {
        lock (_coreOutput) _coreOutput.Clear();
        _xray = StartCore(_xrayPath!, "run", "-c", _xrayConfigPath!, OnXrayLine, OnXrayExited);
        if (_xray == null)
        {
            FailStart("Не удалось запустить xray.exe");
            return false;
        }

        return true;
    }

    private bool StartSingbox()
    {
        _singbox = StartCore(_singboxPath!, "run", "-c", _singboxConfigPath!, OnSingboxLine, OnSingboxExited, "--disable-color");
        if (_singbox == null)
        {
            FailStart("Не удалось запустить sing-box.exe");
            return false;
        }

        return true;
    }

    private Process? StartCore(string exe, string arg1, string arg2, string config, DataReceivedEventHandler onLine, EventHandler onExited, string? extra = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(arg1);
        psi.ArgumentList.Add(arg2);
        psi.ArgumentList.Add(config);
        if (!string.IsNullOrEmpty(extra))
            psi.ArgumentList.Add(extra);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += onLine;
        proc.ErrorDataReceived += onLine;
        proc.Exited += onExited;
        if (!proc.Start())
        {
            proc.Dispose();
            return null;
        }

        CoreProcessJob.Assign(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static async Task<bool> WaitForSocksAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        ReadOnlyMemory<byte> greeting = new byte[] { 0x05, 0x01, 0x00 };
        var buf = new byte[2];

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(200);
                await tcp.ConnectAsync(IPAddress.Loopback, XrayConfigBuilder.SocksPort, attempt.Token);
                var stream = tcp.GetStream();
                await stream.WriteAsync(greeting, attempt.Token);
                var read = await stream.ReadAsync(buf.AsMemory(0, 2), attempt.Token);
                if (read == 2 && buf[0] == 0x05)
                    return true;
            }
            catch
            {
                await Task.Delay(50, ct);
            }
        }

        return false;
    }

    private async Task WaitForTunAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var adapterLogged = false;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_singbox == null || _singbox.HasExited || _xray == null || _xray.HasExited)
                return;
            if (IsReady)
                return;

            if (NetworkRouteHelper.IsTunUp(SingboxTunConfigBuilder.VpnInterfaceName))
            {
                if (!adapterLogged)
                {
                    adapterLogged = true;
                    AppLogger.Instance.Vpn("адаптер singbox_tun найден, проверка SOCKS…");
                }
                await Task.Delay(250, ct);
                if (_singbox is { HasExited: false } && _xray is { HasExited: false }
                    && NetworkRouteHelper.IsTunUp(SingboxTunConfigBuilder.VpnInterfaceName)
                    && await WaitForSocksAsync(TimeSpan.FromSeconds(2), ct))
                {
                    MarkReady();
                    return;
                }
            }

            await Task.Delay(200, ct);
        }
    }

    private void PinControlRoutes(IEnumerable<IPAddress> ips)
    {
        UnpinControlRoutes();
        if (_wan == null) return;
        _pinnedRoutes.AddRange(ips);
        if (_pinnedRoutes.Count == 0) return;
        NetworkRouteHelper.AddHostRoutes(_pinnedRoutes, _wan);
        AppLogger.Instance.VpnDbg($"хост-маршруты: {_pinnedRoutes.Count}");
    }

    private void UnpinControlRoutes()
    {
        if (_wan != null && _pinnedRoutes.Count > 0)
            NetworkRouteHelper.RemoveHostRoutes(_pinnedRoutes, _wan);
        _pinnedRoutes.Clear();
    }

    private void ScheduleConnectTimeout()
    {
        CancelConnectTimeout();
        _timeoutCts = new CancellationTokenSource();
        var token = _timeoutCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ConnectTimeoutSeconds), token);
                if (!token.IsCancellationRequested && IsConnecting && !IsReady)
                {
                    AppLogger.Instance.VpnErr("Таймаут подключения VPN");
                    Stop();
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void CancelConnectTimeout()
    {
        try { _timeoutCts?.Cancel(); } catch { /* ignore */ }
        _timeoutCts?.Dispose();
        _timeoutCts = null;
    }

    private void OnXrayExited(object? sender, EventArgs e)
    {
        if (_stopping || _softRestarting || (!IsRunning && !IsConnecting)) return;
        if (IsReady && _connectedServer != null)
        {
            AppLogger.Instance.VpnErr($"xray завершился (код {_xray?.ExitCode}), восстановление…");
            _ = TrySoftRestartAsync();
            return;
        }
        FailStart(ReadFailure($"xray завершился (код {_xray?.ExitCode})"));
    }

    private void OnSingboxExited(object? sender, EventArgs e)
    {
        if (_stopping || _softRestarting || (!IsRunning && !IsConnecting)) return;
        if (IsReady && _connectedServer != null)
        {
            AppLogger.Instance.VpnErr($"sing-box завершился (код {_singbox?.ExitCode}), восстановление…");
            _ = TrySoftRestartAsync();
            return;
        }
        FailStart(ReadFailure($"туннель завершился (код {_singbox?.ExitCode})"));
    }

    private void StopProcesses()
    {
        KillProc(ref _singbox, OnSingboxLine, OnSingboxExited);
        KillProc(ref _xray, OnXrayLine, OnXrayExited);
    }

    private static void KillProc(ref Process? proc, DataReceivedEventHandler onLine, EventHandler onExited)
    {
        if (proc == null) return;
        try
        {
            proc.Exited -= onExited;
            proc.OutputDataReceived -= onLine;
            proc.ErrorDataReceived -= onLine;
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(4000);
            }
        }
        catch { /* ignore */ }
        finally
        {
            proc.Dispose();
            proc = null;
        }
    }

    private void OnXrayLine(object sender, DataReceivedEventArgs e) => CaptureLine("xray", e.Data);
    private void OnSingboxLine(object sender, DataReceivedEventArgs e) => CaptureLine("tun", e.Data);

    private void CaptureLine(string source, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (IsNoisy(line)) return;
        AppLogger.Instance.VpnDbg($"[{source}] {line}");
        lock (_coreOutput)
        {
            if (_coreOutput.Length < 4000)
                _coreOutput.AppendLine(line);
        }
    }

    private static bool IsNoisy(string line) =>
        line.Contains("A unified platform", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Xray ", StringComparison.Ordinal) ||
        line.Contains("sing-box version", StringComparison.OrdinalIgnoreCase);

    private string ReadFailure(string fallback)
    {
        string captured;
        lock (_coreOutput) captured = _coreOutput.ToString();
        try
        {
            var logPath = XrayConfigBuilder.ErrorLogPath;
            if (File.Exists(logPath))
            {
                var fileLog = File.ReadAllText(logPath);
                if (!string.IsNullOrWhiteSpace(fileLog))
                    captured = string.IsNullOrWhiteSpace(captured) ? fileLog : captured + fileLog;
            }
        }
        catch { /* ignore */ }

        var detail = captured.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l =>
                l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("FATAL", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(detail))
            return fallback;
        return detail.Length > 220 ? detail[..220] : detail;
    }

    private static void StopBypassIfActive()
    {
        if (BypassManager.Instance.IsRunning)
        {
            AppLogger.Instance.Vpn("Останавливаем обход перед VPN…");
            BypassManager.Instance.Stop();
        }
        WireGuardService.Instance.StopSync();
    }

    private static string ConfigDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ByPassMe");

    private static string? FindTool(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[] { Path.Combine("tools", name), name })
        {
            var path = Path.Combine(baseDir, rel);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void StartCooldown(int seconds)
    {
        CooldownSeconds = seconds;
        Notify();
        _cooldownTask = Task.Run(async () =>
        {
            while (CooldownSeconds > 0)
            {
                await Task.Delay(1000);
                CooldownSeconds--;
                Notify();
            }
        });
    }

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdogCts = new CancellationTokenSource();
        var ct = _watchdogCts.Token;
        _watchdogTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && IsReady && IsRunning)
                {
                    await Task.Delay(TimeSpan.FromSeconds(WatchdogIntervalSeconds), ct);
                    if (!IsReady || _stopping || _softRestarting) continue;

                    if (await CheckHealthFullAsync(ct))
                    {
                        _healthFailCount = 0;
                        continue;
                    }

                    _healthFailCount++;
                    AppLogger.Instance.VpnDbg($"watchdog: проверка не прошла ({_healthFailCount}/2)");
                    if (_healthFailCount >= 2)
                    {
                        _healthFailCount = 0;
                        await TrySoftRestartAsync();
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private void StopWatchdog()
    {
        try { _watchdogCts?.Cancel(); } catch { /* ignore */ }
        _watchdogCts?.Dispose();
        _watchdogCts = null;
        _watchdogTask = null;
    }

    private async Task<bool> CheckHealthFullAsync(CancellationToken ct)
    {
        if (_xray == null || _xray.HasExited) return false;
        if (_singbox == null || _singbox.HasExited) return false;
        if (!NetworkRouteHelper.IsTunUp(SingboxTunConfigBuilder.VpnInterfaceName)) return false;
        return await WaitForSocksAsync(TimeSpan.FromSeconds(3), ct);
    }

    private async Task TrySoftRestartAsync()
    {
        if (_softRestarting || _connectedServer == null || _connectedUuid == null || !IsRunning)
            return;

        if ((DateTime.UtcNow - _softRestartWindowStart) > SoftRestartWindow)
        {
            _softRestartCount = 0;
            _softRestartWindowStart = DateTime.UtcNow;
        }

        if (_softRestartCount >= MaxSoftRestartsPerWindow)
        {
            FailStart("Туннель нестабилен — переподключитесь вручную");
            return;
        }

        _softRestartCount++;
        _softRestarting = true;
        IsReady = false;
        Notify();
        AppLogger.Instance.Vpn($"watchdog: мягкий перезапуск ({_softRestartCount}/{MaxSoftRestartsPerWindow})…");

        try
        {
            _stopping = true;
            StopProcesses();
            NetworkRouteHelper.RemoveTunAdapter();
            await Task.Delay(400);

            if (_wan == null)
            {
                _wan = NetworkRouteHelper.DetectWan();
                if (_wan == null)
                {
                    FailStart("Нет активного сетевого подключения");
                    return;
                }
            }

            var server = _connectedServer;
            var uuid = _connectedUuid;
            var connectIp = _connectIp;
            if (connectIp == null)
            {
                var ips = NetworkRouteHelper.ResolveIpv4(server.Address);
                connectIp = ips.FirstOrDefault()?.ToString();
                _connectIp = connectIp;
                PinControlRoutes(ips);
            }

            await WriteXrayConfigAsync(server, uuid, connectIp, CancellationToken.None);
            if (!StartXray())
                return;

            if (!await WaitForSocksAsync(TimeSpan.FromSeconds(10), CancellationToken.None))
            {
                FailStart(ReadFailure("xray не открыл SOCKS после перезапуска"));
                return;
            }

            await WriteSingboxConfigAsync(server, CancellationToken.None);
            if (!StartSingbox())
                return;

            var deadline = DateTime.UtcNow.AddSeconds(ConnectTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (_singbox == null || _singbox.HasExited || _xray == null || _xray.HasExited)
                    break;
                if (NetworkRouteHelper.IsTunUp(SingboxTunConfigBuilder.VpnInterfaceName)
                    && await WaitForSocksAsync(TimeSpan.FromSeconds(2), CancellationToken.None))
                {
                    _stopping = false;
                    _softRestarting = false;
                    IsReady = true;
                    LastError = null;
                    ConnectionStatsService.Vpn.Start();
                    StartWatchdog();
                    AppLogger.Instance.Vpn("watchdog: туннель восстановлен ✓");
                    Notify();
                    return;
                }
                await Task.Delay(400);
            }

            FailStart(ReadFailure("Не удалось восстановить туннель"));
        }
        catch (Exception ex)
        {
            FailStart($"watchdog: {ex.Message}");
        }
        finally
        {
            _softRestarting = false;
            _stopping = false;
        }
    }

    private void HookNetworkChange()
    {
        if (_networkHooked) return;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _networkHooked = true;
    }

    private void UnhookNetworkChange()
    {
        if (!_networkHooked) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _networkHooked = false;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!IsReady || _softRestarting || _stopping) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            if (!IsReady || _softRestarting || _stopping) return;

            var newWan = NetworkRouteHelper.DetectWan();
            if (newWan == null) return;
            if (_wan != null
                && newWan.Gateway == _wan.Gateway
                && newWan.Name == _wan.Name
                && newWan.Ipv4 == _wan.Ipv4)
                return;

            AppLogger.Instance.Vpn($"Сеть изменилась ({_wan?.Name} → {newWan.Name}), перезапуск VPN…");
            _wan = newWan;
            if (_connectedServer != null)
            {
                var ips = NetworkRouteHelper.ResolveIpv4(_connectedServer.Address);
                PinControlRoutes(ips);
                _connectIp = ips.FirstOrDefault()?.ToString();
            }
            await TrySoftRestartAsync();
        });
    }
}
