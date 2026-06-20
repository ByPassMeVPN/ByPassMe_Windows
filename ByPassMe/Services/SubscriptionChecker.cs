using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ByPassMe.Models;

namespace ByPassMe.Services;

public sealed class SubscriptionChecker
{
    public static SubscriptionChecker Instance { get; } = new();

    private const string Base = "https://sub.bypassme.online";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public string Status { get; private set; } = "unknown";
    public int DaysLeft { get; private set; }
    /// <summary>Сервер подписки недоступен — работаем из локального кэша (как Android).</summary>
    public bool UsingOfflineCache { get; private set; }

    public event Action? Changed;

    public static string ExtractSubKey(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        try
        {
            var uri = new Uri(trimmed);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[^1] : trimmed;
        }
        catch
        {
            return trimmed;
        }
    }

    public async Task<(SubscriptionResult Result, string? ErrorMessage)> FetchAsync(
        string url, bool reconnect = false, CancellationToken ct = default)
    {
        var subKey = ExtractSubKey(url);
        var hwid = DeviceInfo.GetHwid();
        var deviceName = DeviceInfo.GetDeviceName();
        var userAgent = $"ByPassMe/2.0 Windows/{Environment.OSVersion.Version}";

        var lastError = "Нет связи с сервером";
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/meta/{subKey}");
            request.Headers.TryAddWithoutValidation("X-HWID", hwid);
            request.Headers.TryAddWithoutValidation("X-Device-Name", deviceName);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            if (reconnect)
                request.Headers.TryAddWithoutValidation("X-Device-Reconnect", "1");

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            switch ((int)response.StatusCode)
            {
                case 403:
                    var error = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                    }
                    catch { /* ignore */ }

                    return error switch
                    {
                        "device_limit" => (SubscriptionResult.DeviceLimitExceeded, null),
                        "device_blocked" => await RevokeAndReturn(SubscriptionResult.DeviceBlocked),
                        "device_removed" => await RevokeAndReturn(SubscriptionResult.DeviceRemoved),
                        _ => (SubscriptionResult.Error, "Ошибка доступа (403)")
                    };

                case 404:
                    return (SubscriptionResult.Error, "Подписка не найдена. Проверьте ключ.");

                case 200 when !string.IsNullOrEmpty(body):
                    return await ParseAndSaveAsync(url, body, ct);

                default:
                    lastError = $"Ошибка сервера: {(int)response.StatusCode}";
                    break;
            }
        }
        catch
        {
            lastError = "Нет связи с сервером";
        }

        return (SubscriptionResult.Error, lastError);
    }

    private async Task<(SubscriptionResult, string?)> RevokeAndReturn(SubscriptionResult result)
    {
        var reason = result == SubscriptionResult.DeviceBlocked ? "blocked" : "removed";
        RevokeAccess(reason);
        await Task.CompletedTask;
        return (result, null);
    }

    private Task<(SubscriptionResult, string?)> ParseAndSaveAsync(string url, string body, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var daysL = root.TryGetProperty("days_left", out var d) ? d.GetInt32() : 0;
            var uuid = root.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
            var wdttPass = root.TryGetProperty("wdtt_password", out var p) ? p.GetString() ?? "" : "";
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";

            if (string.IsNullOrEmpty(uuid))
            {
                var cached = SettingsStore.Instance.VpnUuid;
                if (!string.IsNullOrEmpty(cached))
                    uuid = cached;
                else
                    return Task.FromResult<(SubscriptionResult, string?)>(
                        (SubscriptionResult.Error, "Пустой ответ сервера (нет uuid)"));
            }

            var newStatus = daysL == 0 && type != "unknown" ? "expired"
                : type == "unknown" ? "unknown" : "active";

            var expireAt = daysL > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + daysL * 86_400_000L
                : 0.0;

            SettingsStore.Instance.SaveVpnCredentialsFull(uuid, url, newStatus, daysL, expireAt);
            if (!string.IsNullOrEmpty(wdttPass))
                SettingsStore.Instance.SaveConnectionPassword(wdttPass);

            Status = newStatus;
            DaysLeft = daysL;
            UsingOfflineCache = false;
            Changed?.Invoke();

            if (newStatus == "expired")
            {
                RevokeAccess("expired");
            }

            return Task.FromResult<(SubscriptionResult, string?)>((SubscriptionResult.Success, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(SubscriptionResult, string?)>((SubscriptionResult.Error, $"Ошибка разбора ответа: {ex.Message}"));
        }
    }

    public void EnterOfflineCacheMode()
    {
        if (!SettingsStore.Instance.HasActiveSession)
            return;

        UsingOfflineCache = true;
        if (Status is not ("active" or "expired"))
            Status = SettingsStore.Instance.VpnStatusString is "active" or "expired"
                ? SettingsStore.Instance.VpnStatusString
                : "unknown";
        Changed?.Invoke();
    }

    public void RevokeAccess(string reason)
    {
        void DoRevoke()
        {
            if (string.IsNullOrEmpty(SettingsStore.Instance.VpnUuid)
                && !SettingsStore.Instance.HasOfflineBypassCache
                && string.IsNullOrEmpty(SettingsStore.Instance.VpnSubscriptionUrl))
                return;

            AppLogger.Instance.Service(
                reason switch
                {
                    "expired" => "Подписка истекла — возврат на онбординг",
                    "blocked" => "Устройство заблокировано — возврат на онбординг",
                    "removed" => "Устройство отключено в боте — возврат на онбординг",
                    _ => $"Доступ отозван ({reason}) — возврат на онбординг"
                });

            SettingsStore.Instance.RevokeVpnAccess(reason);
            BypassServerManager.Instance.LoadCached();
            Status = "unknown";
            DaysLeft = 0;
            UsingOfflineCache = false;
            BypassManager.Instance.Stop();
            Changed?.Invoke();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(DoRevoke, DispatcherPriority.Normal);
        else
            DoRevoke();
    }

    public void LoadCached()
    {
        var store = SettingsStore.Instance;
        var expireAt = store.VpnExpireAt;
        var cached = store.VpnStatusString;
        var days = store.VpnDaysLeft;

        if (expireAt > 0)
        {
            var computed = Math.Max(0, (int)((expireAt / 1000 - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 86400));
            DaysLeft = computed;
            Status = computed == 0 && cached == "active" ? "expired" : cached;
        }
        else
        {
            Status = cached;
            DaysLeft = days;
        }

        Changed?.Invoke();
    }

    public void RefreshInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Instance.ServiceDbg($"Фоновая проверка подписки: {ex.Message}");
            }
        });
    }

    /// <summary>Опрос /meta каждые 5 с — как Android MainActivity (device_removed → онбординг).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var url = SettingsStore.Instance.VpnSubscriptionUrl;
        if (string.IsNullOrEmpty(url))
            return;

        var (result, _) = await FetchAsync(url, ct: ct);

        switch (result)
        {
            case SubscriptionResult.DeviceBlocked:
            case SubscriptionResult.DeviceRemoved:
                // revokeAccess уже вызван внутри FetchAsync при 403
                break;

            case SubscriptionResult.DeviceLimitExceeded:
                // Как на Android: лимит при фоновой проверке
                RevokeAccess("blocked");
                break;

            case SubscriptionResult.Success:
                break;

            case SubscriptionResult.Error:
                // Сетевые ошибки — uuid и кэш остаются для офлайн-работы (как Android)
                if (SettingsStore.Instance.HasActiveSession)
                {
                    EnterOfflineCacheMode();
                    AppLogger.Instance.Service("Подписка: API недоступен — используется локальный кэш");
                }
                break;
        }
    }
}
