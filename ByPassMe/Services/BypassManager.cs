using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ByPassMe.Helpers;
using ByPassMe.Models;

namespace ByPassMe.Services;

/// <summary>
/// Обход Б/С: Go-клиент в режиме rawtun (адаптер wintun внутри клиента) и TURN
/// по TCP — один в один с Android TunnelManager и iOS BypassManager.
/// WireGuard и sing-box в обходе больше не участвуют.
/// </summary>
public sealed class BypassManager
{
    public static BypassManager Instance { get; } = new();

    private Process? _process;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private Task? _cooldownTask;
    private readonly object _stdinLock = new();

    private int _floodCount;
    private int _mismatchCount;
    private int _refusedCount;
    private int _hashErrorCount;
    private bool _captchaSolving;
    private DateTime? _credsReceivedAt;
    private CancellationTokenSource? _stuckCts;
    private Task? _stuckTask;
    private string? _currentPeer;
    private int _dtlsFailCount;
    private readonly HashSet<string> _turnExcludeHosts = new(StringComparer.Ordinal);

    // Держим Обход до кнопки «Отключить»: после первого подключения сбои
    // (сон ПК, сеть, VK) не выключают его, а перезапускают клиент с паузой.
    private string _argPeer = "";
    private string _argHash = "";
    private int _argWorkers;
    private volatile bool _everReady;
    private int _recoveryAttempts;
    private CancellationTokenSource? _recoveryCts;
    private CancellationTokenSource? _healthCts;
    private DateTime _lastStatsAt;
    private int _activeWorkers = -1;
    private DateTime? _zeroWorkersSince;
    private bool _powerHooked;
    private string? _primaryNic;
    private CancellationTokenSource? _netDebounceCts;
    private static readonly Regex ActiveWorkersRx = new(@"Активных:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex VkCallDeadRx = new(@"error_code=(9\d{3}|951|954)\b", RegexOptions.Compiled);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Имя адаптера, который поднимает Go-клиент (wintun).</summary>
    public const string RawTunAdapterName = "ByPassMe-Raw";

    public bool IsRunning { get; private set; }
    public bool IsReady { get; private set; }
    public int CooldownSeconds { get; private set; }
    public string? LastError { get; private set; }

    public event Action? Changed;

    private void Notify() => Changed?.Invoke();

    public async Task StartAsync(string peer, string vkHash, int workers, CancellationToken ct = default)
    {
        if (IsRunning) return;

        if (VpnManager.Instance.IsRunning || VpnManager.Instance.IsConnecting)
        {
            AppLogger.Instance.Bypass("Останавливаем VPN перед обходом…");
            VpnManager.Instance.Stop();
            await Task.Delay(400, ct);
        }

        IsRunning = true;
        IsReady = false;
        LastError = null;
        SettingsStore.Instance.SaveLastMode("bypass");
        _everReady = false;
        _recoveryAttempts = 0;
        _argPeer = peer;
        _argHash = vkHash;
        _argWorkers = workers;
        Notify();
        HookPowerEvents();

        await LaunchAsync();
        if (IsRunning) StartHealthWatch();
    }

    /// <summary>Запуск Go-клиента с сохранёнными параметрами (первый раз и при переподключении).</summary>
    private async Task LaunchAsync()
    {
        var peer = _argPeer;
        var vkHash = _argHash;
        var workers = _argWorkers;

        _floodCount = 0;
        _mismatchCount = 0;
        _refusedCount = 0;
        _hashErrorCount = 0;
        _dtlsFailCount = 0;
        _credsReceivedAt = null;
        _turnExcludeHosts.Clear();

        var binaryPath = FindBypassClient();
        if (binaryPath == null)
        {
            LastError = "bypassclient.exe не найден. Пересоберите проект (build-windows.ps1).";
            AppLogger.Instance.BypassErr(LastError);
            IsRunning = false;
            Notify();
            return;
        }

        var deviceId = DeviceInfo.GetHwid();
        var password = string.IsNullOrEmpty(SettingsStore.Instance.ConnectionPassword)
            ? "ByPassMe"
            : SettingsStore.Instance.ConnectionPassword;

        workers = Math.Clamp(workers, 9, 27);
        workers = (workers / 9) * 9;
        if (workers < 9) workers = 9;

        var peerAddr = peer.Contains(':') ? peer : $"{peer}:{BypassServer.DefaultRawPort}";
        _currentPeer = peerAddr;

        _credsReceivedAt = null;
        _workerReadyLogged = false;
        AppLogger.Instance.Bypass($"Запуск: peer={peerAddr}, workers={workers}, captcha=auto, turn=tcp, режим=rawtun, wrap=on");

        // Адреса VK и доменов «напрямую» получаем сами — при глушении резолвер
        // внутри Go-клиента может молчать (см. BypassNetworkPrep).
        var vkHostIps = await BypassNetworkPrep.VkHostIpsJsonAsync(TimeSpan.FromSeconds(2));
        var directIps = await BypassNetworkPrep.DirectDomainIpsAsync(TimeSpan.FromSeconds(2));
        var dnsFallback = BypassNetworkPrep.FallbackDnsCsv();
        AppLogger.Instance.Bypass($"[СЕТЬ] IP хостов VK получены, «напрямую»: {directIps.Split(',', StringSplitOptions.RemoveEmptyEntries).Length} адресов");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = Path.GetDirectoryName(binaryPath) ?? AppContext.BaseDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-peer"); psi.ArgumentList.Add(peerAddr);
            psi.ArgumentList.Add("-vk"); psi.ArgumentList.Add(vkHash);
            psi.ArgumentList.Add("-n"); psi.ArgumentList.Add(workers.ToString());
            psi.ArgumentList.Add("-listen"); psi.ArgumentList.Add("127.0.0.1:9000");
            psi.ArgumentList.Add("-device-id"); psi.ArgumentList.Add(deviceId);
            psi.ArgumentList.Add("-password"); psi.ArgumentList.Add(password);
            psi.ArgumentList.Add("-captcha-mode"); psi.ArgumentList.Add("auto");
            // «Голосовой» RTP-заголовок — только если сервер его понимает (voice в списке).
            var primaryHost = peerAddr.Split(',')[0];
            primaryHost = primaryHost.Contains(':') ? primaryHost[..primaryHost.LastIndexOf(':')] : primaryHost;
            if (BypassServerManager.Instance.Servers.Any(sv => sv.Host == primaryHost && sv.Voice))
                psi.ArgumentList.Add("-obfs-voice");
            psi.ArgumentList.Add("-vk-auth"); psi.ArgumentList.Add("anonymous");
            psi.ArgumentList.Add("-vk-anon-path"); psi.ArgumentList.Add("vkcalls");
            // Единственный режим: TUN внутри клиента + TURN по TCP (сервер -listen-raw).
            psi.ArgumentList.Add("-mode"); psi.ArgumentList.Add("rawtun");
            psi.ArgumentList.Add("-turn-tcp");
            psi.ArgumentList.Add("-tun-name"); psi.ArgumentList.Add(RawTunAdapterName);
            psi.ArgumentList.Add("-go-dns"); psi.ArgumentList.Add("system");
            if (!string.IsNullOrEmpty(dnsFallback))
            {
                psi.ArgumentList.Add("-dns-fallback"); psi.ArgumentList.Add(dnsFallback);
            }
            if (!string.IsNullOrEmpty(vkHostIps))
            {
                psi.ArgumentList.Add("-vk-host-ips"); psi.ArgumentList.Add(vkHostIps);
            }
            if (!string.IsNullOrEmpty(directIps))
            {
                psi.ArgumentList.Add("-exclude-ips"); psi.ArgumentList.Add(directIps);
            }

            // Пока готовились адреса VK (до 4 с), могли нажать «Отключить».
            if (!IsRunning) return;

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process = proc;
            _lastStatsAt = DateTime.UtcNow;
            _activeWorkers = -1;
            _zeroWorkersSince = null;

            proc.Exited += (_, _) =>
            {
                // Старый процесс, который мы сами сняли при переподключении, — не в счёт.
                if (!ReferenceEquals(_process, proc) || !IsRunning) return;
                if (_everReady)
                {
                    AppLogger.Instance.BypassErr("Go-клиент завершился — переподключаем, Обход не выключаем");
                    ScheduleRecovery("клиент завершился");
                    return;
                }
                IsRunning = false;
                IsReady = false;
                LastError ??= "Процесс обхода завершился";
                AppLogger.Instance.BypassErr("Go-клиент завершился");
                Notify();
            };

            proc.Start();
            proc.BeginErrorReadLine();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    HandleLogLine(e.Data);
            };

            _readerCts = new CancellationTokenSource();
            var readerCt = _readerCts.Token;
            _readerTask = Task.Run(() => ReadOutputAsync(proc, readerCt), readerCt);
            StartStuckConnectionWatch();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.BypassErr($"Ошибка запуска: {ex.Message}");
            if (_everReady && IsRunning)
            {
                ScheduleRecovery("ошибка запуска клиента");
                return;
            }
            LastError = ex.Message;
            IsRunning = false;
            Notify();
        }
    }

    /// <summary>
    /// Сбой после того, как Обход уже работал: не выключаем, а перезапускаем
    /// клиент с нарастающей паузой (2 с, 15, 30, 60 … до 5 мин). Если первое
    /// подключение ещё не удалось — стоп с причиной, как раньше.
    /// </summary>
    private void RecoverOrStop(string error, int minDelayMs = 0)
    {
        if (_everReady && IsRunning)
        {
            AppLogger.Instance.BypassErr($"⚠ {error} — переподключаемся, Обход не выключаем");
            ScheduleRecovery(error, minDelayMs);
            return;
        }
        LastError = error;
        AppLogger.Instance.BypassErr(error);
        Stop(clearError: false);
    }

    private void ScheduleRecovery(string reason, int minDelayMs = 0)
    {
        if (!IsRunning) return;
        var attempt = _recoveryAttempts++;
        var backoff = attempt == 0 ? 2_000 : Math.Min(15_000 << Math.Min(attempt - 1, 5), 300_000);
        var delayMs = Math.Max(backoff, minDelayMs) + Random.Shared.Next(0, 2_000);
        IsReady = false;
        AppLogger.Instance.Bypass($"[СЕТЬ] Переподключение через {delayMs / 1000} с ({reason}, попытка {attempt + 1})");
        Notify();

        _recoveryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _recoveryCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                StopProcessOnly();
                await Task.Delay(delayMs, cts.Token);
                if (IsRunning && !cts.IsCancellationRequested)
                    await LaunchAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Instance.BypassErr($"Переподключение: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Смена основной сети (Wi-Fi ↔ кабель ↔ модем): сокеты к TURN VK мертвы.
    /// Свой адаптер Обхода не считается — иначе его подъём выглядел бы сменой
    /// сети. Пауза 2 с: события адресов идут пачкой.
    /// </summary>
    private void OnNetworkAddressChanged()
    {
        _netDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _netDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { return; }
            var now = PrimaryInterfaceId();
            var prev = _primaryNic;
            if (now == null || now == prev) { if (now != null) _primaryNic = now; return; }
            _primaryNic = now;
            if (prev == null || !IsRunning || !_everReady) return;
            AppLogger.Instance.Bypass("Сменилась сеть — переподключаем потоки Обхода");
            try { WriteStdin("RECONNECT|сменилась сеть"); } catch { /* ignore */ }
        });
    }

    /// <summary>Id активного физического адаптера со шлюзом по умолчанию (не Обход, не loopback/туннели).</summary>
    private static string? PrimaryInterfaceId()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                if (nic.Name.Contains(RawTunAdapterName, StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)) continue;
                var props = nic.GetIPProperties();
                if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                    && !g.Address.Equals(System.Net.IPAddress.Any)))
                    return nic.Id;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Снять только Go-процесс (адаптер уберёт сам клиент), не меняя IsRunning.</summary>
    private void StopProcessOnly()
    {
        var proc = _process;
        _process = null; // до STOP: Exited старого процесса не должен запускать восстановление
        _readerCts?.Cancel();
        _stuckCts?.Cancel();
        _stuckCts = null;
        if (proc == null) return;
        try
        {
            lock (_stdinLock)
            {
                proc.StandardInput.WriteLine("STOP");
                proc.StandardInput.Flush();
            }
        }
        catch { /* ignore */ }
        try { if (!proc.HasExited) proc.WaitForExit(3000); } catch { /* ignore */ }
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    /// <summary>
    /// Сторож: 0 активных воркеров 90 с или нет статистики 2 мин (при
    /// уже работавшем Обходе) — переподключение.
    /// </summary>
    private void StartHealthWatch()
    {
        _healthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _healthCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested && IsRunning)
                {
                    await Task.Delay(10_000, cts.Token);
                    if (!_everReady || _captchaSolving || _process == null) continue;
                    var now = DateTime.UtcNow;
                    if (_activeWorkers == 0)
                    {
                        _zeroWorkersSince ??= now;
                        if ((now - _zeroWorkersSince.Value).TotalSeconds > 90)
                        {
                            _zeroWorkersSince = null;
                            ScheduleRecovery("0 воркеров 90 с");
                            continue;
                        }
                    }
                    else
                    {
                        _zeroWorkersSince = null;
                    }
                    if ((now - _lastStatsAt).TotalSeconds > 120)
                    {
                        _lastStatsAt = now;
                        ScheduleRecovery("нет статистики 2 мин");
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Выход ПК из сна: даём клиенту 2 мин переподключить воркеры сам, дальше — сторож.</summary>
    private void HookPowerEvents()
    {
        if (_powerHooked) return;
        _powerHooked = true;
        try
        {
            _primaryNic = PrimaryInterfaceId();
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkAddressChanged();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.BypassDbg($"NetworkAddressChanged недоступен: {ex.Message}");
        }
        try
        {
            SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (!IsRunning) return;
                if (e.Mode == PowerModes.Resume)
                {
                    _lastStatsAt = DateTime.UtcNow;
                    _zeroWorkersSince = null;
                    AppLogger.Instance.Bypass("Выход из сна — проверяем потоки Обхода");
                    // Клиент сам проверит потоки (ответ сервера на keepalive) и
                    // пересоздаст молчащие; старый клиент команду проигнорирует.
                    try { WriteStdin("WAKE"); } catch { /* ignore */ }
                }
                else if (e.Mode == PowerModes.Suspend)
                {
                    AppLogger.Instance.Bypass("Компьютер уходит в сон");
                }
            };
        }
        catch (Exception ex)
        {
            AppLogger.Instance.BypassDbg($"PowerModeChanged недоступен: {ex.Message}");
        }
    }

    public void Stop(bool clearError = true)
    {
        var wasRunning = IsRunning;
        IsRunning = false; // сразу: обработчики выхода процесса не должны запускать восстановление
        _recoveryCts?.Cancel();
        _recoveryCts = null;
        _healthCts?.Cancel();
        _healthCts = null;

        // Сначала STOP: клиент сам снимает маршруты и убирает адаптер. Если не
        // успел за 3 с — снимаем процесс, адаптер освободит драйвер wintun.
        try
        {
            WriteStdin("STOP");
        }
        catch { /* ignore */ }

        _readerCts?.Cancel();
        _stuckCts?.Cancel();
        _stuckCts = null;

        if (_process is { HasExited: false })
        {
            try { _process.WaitForExit(3000); }
            catch { /* ignore */ }
        }
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* ignore */ }
        }
        _process = null;

        IsRunning = false;
        IsReady = false;
        ConnectionStatsService.Bypass.Stop();
        if (clearError) LastError = null;
        AppLogger.Instance.Bypass("Обход Б/С остановлен");
        Notify();

        if (wasRunning) StartCooldown(5);
    }

    public void StartCooldown(int seconds)
    {
        _cooldownTask?.Dispose();
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

    private async Task ReadOutputAsync(Process proc, CancellationToken ct)
    {
        var lastReset = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested && !proc.HasExited)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                HandleLogLine(line, ref lastReset);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Instance.BypassErr($"Чтение лога: {ex.Message}");
        }
        // Выход процесса обрабатывает proc.Exited (там же переподключение);
        // раньше здесь ставилось IsRunning=false и Обход выключался целиком.
    }

    private void HandleLogLine(string line) => HandleLogLine(line, ref _lastReset);

    private DateTime _lastReset = DateTime.UtcNow;
    private bool _workerReadyLogged;

    private void HandleLogLine(string line, ref DateTime lastReset)
    {
        var now = DateTime.UtcNow;
        if ((now - lastReset).TotalSeconds > 60)
        {
            _floodCount = 0;
            _mismatchCount = 0;
            _refusedCount = 0;
            _hashErrorCount = 0;
            lastReset = now;
        }

        var lineTrim = Regex.Replace(line, @"^\d{4}/\d{2}/\d{2}\s\d{2}:\d{2}:\d{2}(\.\d+)?\s", "").Trim();
        var isError = lineTrim.Contains("Ошибка", StringComparison.OrdinalIgnoreCase)
            || lineTrim.Contains("error", StringComparison.OrdinalIgnoreCase)
            || lineTrim.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
            || lineTrim.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || lineTrim.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || lineTrim.Contains("FATAL_AUTH", StringComparison.OrdinalIgnoreCase);

        if (lineTrim.Contains("FATAL_AUTH", StringComparison.OrdinalIgnoreCase))
        {
            var reason = lineTrim.Contains("неверный пароль") ? "Неверный пароль подключения"
                : lineTrim.Contains("истёк") ? "Срок действия пароля истёк"
                : lineTrim.Contains("другому устройству") ? "Пароль привязан к другому устройству"
                : "Ошибка авторизации";
            LastError = $"🔒 {reason}";
            AppLogger.Instance.BypassErr(LastError);
            Stop(clearError: false);
            return;
        }

        if (lineTrim.StartsWith("TURN_EXCLUDE|", StringComparison.Ordinal))
        {
            foreach (var ip in lineTrim["TURN_EXCLUDE|".Length..]
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _turnExcludeHosts.Add(ip);
            AppLogger.Instance.BypassDbg($"Split-tunnel: TURN {string.Join(", ", _turnExcludeHosts)}");
            return;
        }

        var turnUdp = Regex.Match(lineTrim, @"TURN UDP \(([0-9.]+):");
        if (turnUdp.Success)
            _turnExcludeHosts.Add(turnUdp.Groups[1].Value);

        var turnUrl = Regex.Match(lineTrim, @"turn:([\d.]+):", RegexOptions.IgnoreCase);
        if (turnUrl.Success)
            _turnExcludeHosts.Add(turnUrl.Groups[1].Value);

        if (lineTrim.StartsWith("CAPTCHA_SOLVE|"))
        {
            // Go: CAPTCHA_SOLVE|mode|redirectURI|sessionToken  (как Android TunnelManager)
            var payload = lineTrim["CAPTCHA_SOLVE|".Length..];
            var parts = payload.Split('|', 3);
            if (parts.Length == 3)
                _ = HandleCaptchaSolveAsync(parts[1], parts[2]);
            else if (parts.Length == 2)
                _ = HandleCaptchaSolveAsync(parts[0], parts[1]);
            else
                WriteStdin("CAPTCHA_RESULT|error:invalid CAPTCHA_SOLVE format");
            return;
        }

        if (lineTrim.Contains("[КАПЧА] WV:", StringComparison.OrdinalIgnoreCase)
            || lineTrim.Contains("[КАПЧА] WBV:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = lineTrim.IndexOf("[КАПЧА]", StringComparison.Ordinal);
            var text = idx >= 0 ? lineTrim[(idx + 7)..].Trim() : lineTrim;
            AppLogger.Instance.Bypass($"[КАПЧА WBV] {text}");
            return;
        }

        if (lineTrim.Contains("[КАПЧА] RJS:", StringComparison.Ordinal))
        {
            var text = lineTrim[(lineTrim.IndexOf("[КАПЧА] RJS:", StringComparison.Ordinal) + 12)..].Trim();
            AppLogger.Instance.Bypass($"[КАПЧА RJS] {text}");
            return;
        }

        if (lineTrim.Contains("[КАПЧА] ОШИБКА решения", StringComparison.OrdinalIgnoreCase))
        {
            RecoverOrStop("Не удалось решить капчу VK — попробуйте снова", 60_000);
            return;
        }

        if (isError && !_captchaSolving)
        {
            if (lineTrim.Contains("Flood control", StringComparison.OrdinalIgnoreCase) && ++_floodCount >= 5)
            {
                _floodCount = 0;
                RecoverOrStop("Flood Control (ВК ограничил ваш IP)", 60_000);
                return;
            }
            if (lineTrim.Contains("ip mismatch", StringComparison.OrdinalIgnoreCase) && ++_mismatchCount >= 5)
            {
                _mismatchCount = 0;
                RecoverOrStop("IP Mismatch", 15_000);
                return;
            }
            if ((lineTrim.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                 || lineTrim.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                && ++_refusedCount >= 400)
            {
                _refusedCount = 0;
                RecoverOrStop("Критическое отсутствие сети", 30_000);
                return;
            }
            // Только ошибка VK «звонок не найден»: раньше ловилась любая строка
            // с «9000» — это ещё и локальный порт 127.0.0.1:9000.
            if ((VkCallDeadRx.IsMatch(lineTrim) || lineTrim.Contains("Call not found", StringComparison.OrdinalIgnoreCase))
                && ++_hashErrorCount >= 10)
            {
                _hashErrorCount = 0;
                RecoverOrStop("Хеш VK недействителен", 60_000);
                return;
            }
            AppLogger.Instance.BypassErr(lineTrim);
        }
        else if (lineTrim.Contains("[СТАТИСТИКА]"))
        {
            _lastStatsAt = DateTime.UtcNow;
            var m = ActiveWorkersRx.Match(lineTrim);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) _activeWorkers = n;
            AppLogger.Instance.Bypass(lineTrim);
        }
        else if (lineTrim.Contains("Креды OK") || lineTrim.Contains("Первые креды") || lineTrim.Contains("Креды получены"))
        {
            _credsReceivedAt ??= DateTime.UtcNow;
            AppLogger.Instance.Bypass("[ВК] Учетные данные проверены ✓");
        }
        else if (lineTrim.Contains("DTLS ОК") || lineTrim.Contains("[DTLS] Соединение установлено"))
        {
            AppLogger.Instance.Bypass("[DTLS] Соединение установлено ✓");
        }
        else if (lineTrim.Contains("[ВОРКЕР") && lineTrim.Contains("Ошибка (попытка"))
        {
            if (lineTrim.Contains("DTLS", StringComparison.OrdinalIgnoreCase))
                AppLogger.Instance.Bypass(lineTrim);
            else
                AppLogger.Instance.BypassDbg(lineTrim);
            if (lineTrim.Contains("DTLS хендшейк", StringComparison.OrdinalIgnoreCase)
                && lineTrim.Contains("deadline exceeded", StringComparison.OrdinalIgnoreCase))
            {
                _dtlsFailCount++;
                if (_dtlsFailCount >= 3 && !IsReady)
                {
                    LastError = $"Сервер {_currentPeer ?? "VPS"} не отвечает. Проверьте сервер Обхода и порт 56003, или выберите другой сервер.";
                    AppLogger.Instance.BypassErr(LastError);
                    Notify();
                }
            }
        }
        else if (lineTrim.Contains("[RAW] TUN подключён", StringComparison.Ordinal)
                 || lineTrim.Contains("[RAW] Адаптер", StringComparison.Ordinal) && lineTrim.Contains("поднят", StringComparison.Ordinal))
        {
            AppLogger.Instance.Bypass(lineTrim);
            if (lineTrim.Contains("TUN подключён", StringComparison.Ordinal))
            {
                IsReady = true;
                _everReady = true;
                _recoveryAttempts = 0;
                LastError = null;
                ConnectionStatsService.Bypass.Start();
                AppLogger.Instance.Bypass("[READY] Обход Б/С готов к работе ✓");
                Notify();
            }
        }
        else if (lineTrim.Contains("Активна ✓") || lineTrim.Contains("[READY]"))
        {
            // Строка приходит от каждого воркера — в лог пишем только первую.
            if (!_workerReadyLogged)
            {
                _workerReadyLogged = true;
                AppLogger.Instance.Bypass("[READY] Воркеры подключены ✓");
            }
        }
        else if (lineTrim.Contains("[КАПЧА]"))
        {
            AppLogger.Instance.Bypass(lineTrim);
        }
        else if (!string.IsNullOrWhiteSpace(lineTrim))
        {
            AppLogger.Instance.Bypass(lineTrim);
        }
    }

    private async Task HandleCaptchaSolveAsync(string redirectUri, string sessionToken)
    {
        _captchaSolving = true;
        Notify();
        try
        {
            AppLogger.Instance.Bypass("[КАПЧА WBV] Скрытый WebView (авто)...");
            var token = await CaptchaWebViewService.Instance.SolveAsync(redirectUri);
            WriteStdin($"CAPTCHA_RESULT|{token}");
            AppLogger.Instance.Bypass("[КАПЧА WBV] Токен получен ✓");
            LastError = null;
        }
        catch (Exception ex)
        {
            WriteStdin($"CAPTCHA_RESULT|error:{ex.Message}");
            LastError = $"Капча VK: {ex.Message}";
            AppLogger.Instance.BypassErr($"[КАПЧА WBV] {ex.Message}");
        }
        finally
        {
            _captchaSolving = false;
            Notify();
        }
    }

    private void WriteStdin(string line)
    {
        lock (_stdinLock)
        {
            if (_process?.StandardInput == null) return;
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
    }

    private void StartStuckConnectionWatch()
    {
        _stuckCts?.Cancel();
        _stuckCts = new CancellationTokenSource();
        var ct = _stuckCts.Token;
        _stuckTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5000, ct);
                    if (!IsRunning || IsReady || _credsReceivedAt == null) continue;
                    if ((DateTime.UtcNow - _credsReceivedAt.Value).TotalSeconds < 90) continue;

                    LastError = $"Сервер {_currentPeer ?? "VPS"} не отвечает. Проверьте сервер Обхода и порт 56003, или выберите другой сервер.";
                    AppLogger.Instance.BypassErr(LastError);
                    Notify();
                    return;
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private static string? FindBypassClient()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bypassclient.exe"),
            Path.Combine(AppContext.BaseDirectory, "go_client", "bypassclient.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "go_client", "bypassclient.exe"),
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string? FindTool(string name)
    {
        foreach (var rel in new[] { Path.Combine("tools", name), name })
        {
            var path = Path.Combine(AppContext.BaseDirectory, rel);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
