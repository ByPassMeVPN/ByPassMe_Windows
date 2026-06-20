using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ByPassMe.Helpers;
using ByPassMe.Models;

namespace ByPassMe.Services;

/// <summary>Оркестрация Go-клиента + WireGuard — аналог Android TunnelManager / iOS BypassManager.</summary>
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

    public bool IsRunning { get; private set; }
    public bool IsReady { get; private set; }
    public int CooldownSeconds { get; private set; }
    public string? LastError { get; private set; }

    public event Action? Changed;

    private void Notify() => Changed?.Invoke();

    public async Task StartAsync(string peer, string vkHash, int workers, CancellationToken ct = default)
    {
        if (IsRunning) return;

        IsRunning = true;
        IsReady = false;
        LastError = null;
        Notify();

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

        workers = Math.Clamp(workers, 18, 36);
        workers = (workers / 9) * 9;
        if (workers < 18) workers = 18;

        var peerAddr = peer.Contains(':') ? peer : $"{peer}:56000";
        _currentPeer = peerAddr;

        _credsReceivedAt = null;
        AppLogger.Instance.Bypass($"Запуск: peer={peerAddr}, workers={workers}, captcha=rjs, turn=udp, wrap=on");
        AppLogger.Instance.BypassDbg($"bypassclient -peer {peerAddr} -vk ... -n {workers} -password ***");

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
            psi.ArgumentList.Add("-captcha-mode"); psi.ArgumentList.Add("rjs");

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _process.Exited += (_, _) =>
            {
                if (IsRunning)
                {
                    IsRunning = false;
                    IsReady = false;
                    LastError ??= "Процесс обхода завершился";
                    AppLogger.Instance.BypassErr("Go-клиент завершился");
                    Notify();
                }
            };

            _process.Start();
            _process.BeginErrorReadLine();
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    HandleLogLine(e.Data);
            };

            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadOutputAsync(_readerCts.Token), _readerCts.Token);
            StartStuckConnectionWatch();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLogger.Instance.BypassErr($"Ошибка запуска: {ex.Message}");
            IsRunning = false;
            Notify();
        }

        await Task.CompletedTask;
    }

    public void Stop(bool clearError = true)
    {
        var wasRunning = IsRunning;

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
            try { _process.Kill(entireProcessTree: true); }
            catch { /* ignore */ }
        }
        _process = null;

        try
        {
            WireGuardService.Instance.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.VpnErr($"Остановка WG: {ex.Message}");
            WireGuardService.Instance.StopSync();
        }

        IsRunning = false;
        IsReady = false;
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

    private async Task ReadOutputAsync(CancellationToken ct)
    {
        if (_process?.StandardOutput == null) return;

        var collectingConfig = false;
        var configBuilder = new StringBuilder();
        var lastReset = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested && _process is { HasExited: false })
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                HandleLogLine(line, ref collectingConfig, configBuilder, ref lastReset);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Instance.BypassErr($"Чтение лога: {ex.Message}");
        }
        finally
        {
            if (IsRunning)
            {
                IsRunning = false;
                IsReady = false;
                Notify();
            }
        }
    }

    private void HandleLogLine(string line) =>
        HandleLogLine(line, ref _collectingConfig, _configBuilder, ref _lastReset);

    private bool _collectingConfig;
    private readonly StringBuilder _configBuilder = new();
    private DateTime _lastReset = DateTime.UtcNow;

    private void HandleLogLine(string line, ref bool collectingConfig, StringBuilder configBuilder, ref DateTime lastReset)
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
            Stop();
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
            var payload = lineTrim["CAPTCHA_SOLVE|".Length..];
            var parts = payload.Split('|', 2);
            if (parts.Length == 2)
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
            LastError = "Не удалось решить капчу VK — попробуйте снова";
            AppLogger.Instance.BypassErr(LastError);
            Stop(clearError: false);
            return;
        }

        if (isError && !_captchaSolving)
        {
            if (lineTrim.Contains("Flood control", StringComparison.OrdinalIgnoreCase) && ++_floodCount >= 5)
            {
                LastError = "Flood Control (ВК ограничил ваш IP)";
                Stop();
                return;
            }
            if (lineTrim.Contains("ip mismatch", StringComparison.OrdinalIgnoreCase) && ++_mismatchCount >= 5)
            {
                LastError = "IP Mismatch";
                Stop();
                return;
            }
            if ((lineTrim.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                 || lineTrim.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                && ++_refusedCount >= 400)
            {
                LastError = "Критическое отсутствие сети";
                Stop();
                return;
            }
            if ((lineTrim.Contains("9000") || lineTrim.Contains("Call not found", StringComparison.OrdinalIgnoreCase))
                && ++_hashErrorCount >= 10)
            {
                LastError = "Хеш VK недействителен";
                Stop();
                return;
            }
            AppLogger.Instance.BypassErr(lineTrim);
        }
        else if (lineTrim.Contains("[СТАТИСТИКА]"))
        {
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
                    LastError = $"Сервер {_currentPeer ?? "VPS"} не отвечает (DTLS). Проверьте wdtt-server на VPS и UDP-порт 56000, или выберите другой сервер.";
                    AppLogger.Instance.BypassErr(LastError);
                    Notify();
                }
            }
        }
        else if (lineTrim.Contains("Активна ✓") || lineTrim.Contains("[READY]"))
        {
            AppLogger.Instance.Bypass("[READY] Туннель готов к работе ✓");
        }
        else if (lineTrim.Contains("[КАПЧА]"))
        {
            AppLogger.Instance.Bypass(lineTrim);
        }
        else if (!string.IsNullOrWhiteSpace(lineTrim))
        {
            AppLogger.Instance.Bypass(lineTrim);
        }

        // WireGuard config parsing (boxed format from Go client)
        if (line.Contains('╔') && line.Contains("WireGuard"))
        {
            collectingConfig = true;
            configBuilder.Clear();
            return;
        }

        if (collectingConfig)
        {
            if (line.Contains('╚'))
            {
                collectingConfig = false;
                var configStr = configBuilder.ToString().Trim();
                _ = ApplyWireGuardConfigAsync(configStr);
            }
            else if (line.Contains('║'))
            {
                var content = line.Replace("║", "").Trim();
                if (!string.IsNullOrEmpty(content))
                    configBuilder.AppendLine(content);
            }
        }
    }

    private async Task ApplyWireGuardConfigAsync(string config)
    {
        try
        {
            var peerHost = WireGuardConfigHelper.ExtractHost(_currentPeer ?? "");
            config = WireGuardConfigHelper.ApplySplitTunnel(config, peerHost, _turnExcludeHosts);

            AppLogger.Instance.Bypass(
                $"WireGuard конфиг ({config.Length} байт), split-tunnel: VPS + VK + {_turnExcludeHosts.Count} TURN");
            await WireGuardService.Instance.StartAsync(config);
            IsReady = true;
            LastError = null;
            AppLogger.Instance.Bypass("[READY] Обход Б/С готов к работе ✓");
            Notify();
        }
        catch (Exception ex)
        {
            LastError = $"Ошибка запуска VPN: {ex.Message}";
            AppLogger.Instance.BypassErr(LastError);
            Stop(clearError: false);
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

                    LastError = $"Сервер {_currentPeer ?? "VPS"} не отвечает (DTLS). Проверьте wdtt-server и UDP 56000, или выберите другой сервер.";
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
}
