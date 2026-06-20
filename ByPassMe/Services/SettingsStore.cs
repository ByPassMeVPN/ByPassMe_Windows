using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ByPassMe.Models;

namespace ByPassMe.Services;

/// <summary>Хранение настроек — аналог Android SettingsStore / iOS SettingsStore.</summary>
public sealed class SettingsStore
{
    public static SettingsStore Instance { get; } = new();

    private readonly string _dir;
    private readonly string _path;
    private SettingsData _data;

    private SettingsStore()
    {
        _dir = ResolveSettingsDir();
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
        _data = Load();
        WriteDiag(
            $"init path={_path} uuid={!string.IsNullOrEmpty(_data.VpnUuid)} " +
            $"sub={!string.IsNullOrEmpty(_data.VpnSubscriptionUrl)} vk={!string.IsNullOrEmpty(_data.VkCallUrl)}");
    }

    public string Peer => _data.Peer;
    public string VkHashes => _data.VkHashes;
    public string VkCallUrl => _data.VkCallUrl;
    public int WorkersPerHash => _data.WorkersPerHash;
    public int BypassServerIndex => _data.BypassServerIndex;

    public string VpnUuid => _data.VpnUuid;
    public string VpnSubscriptionUrl => _data.VpnSubscriptionUrl;
    public string VpnSubKey => _data.VpnSubKey;
    public double VpnExpireAt => _data.VpnExpireAt;
    public string VpnStatusString => _data.VpnStatusString;
    public int VpnDaysLeft => _data.VpnDaysLeft;
    public string VpnRevokeReason => _data.VpnRevokeReason;

    public string BypassServersJson => _data.BypassServersJson;
    public string ConnectionPassword => _data.ConnectionPassword;
    public double VpnStatusLastCheck => _data.VpnStatusLastCheck;

    /// <summary>Доступ к обходу: активный uuid или полный офлайн-кэш (пароль + серверы).</summary>
    public bool HasActiveSession =>
        !string.IsNullOrEmpty(VpnUuid) || HasOfflineBypassCache;

    /// <summary>Подписка и VK сохранены — онбординг покажет их без повторного ввода.</summary>
    public bool HasPersistedBypassSetup =>
        !string.IsNullOrEmpty(VpnSubscriptionUrl)
        && (!string.IsNullOrEmpty(VkCallUrl) || !string.IsNullOrEmpty(VkHashes));

    /// <summary>Пароль + серверы + ключ подписки — достаточно для подключения без /meta.</summary>
    public bool HasOfflineBypassCache =>
        !string.IsNullOrEmpty(ConnectionPassword)
        && !string.IsNullOrEmpty(BypassServersJson)
        && (!string.IsNullOrEmpty(VpnSubscriptionUrl) || !string.IsNullOrEmpty(VpnSubKey));

    public string ThemeMode => _data.ThemeMode;
    public bool IsDynamicColor => _data.IsDynamicColor;
    public string ThemePalette => _data.ThemePalette;

    public string SettingsFilePath => _path;

    public event Action? Changed;

    public void SaveBypass(string peer, string vkHashes, int workersPerHash)
    {
        _data.Peer = peer;
        _data.VkHashes = vkHashes;
        _data.WorkersPerHash = workersPerHash;
        Persist();
    }

    public void SaveVkCallUrl(string url)
    {
        _data.VkCallUrl = url.Trim();
        Persist();
    }

    public void SaveSubscriptionUrlDraft(string url)
    {
        var trimmed = url.Trim();
        if (_data.VpnSubscriptionUrl == trimmed)
            return;

        _data.VpnSubscriptionUrl = trimmed;
        var subKey = SubscriptionChecker.ExtractSubKey(trimmed);
        if (!string.IsNullOrEmpty(subKey))
            _data.VpnSubKey = subKey;
        Persist();
    }

    public void SaveBypassServerIndex(int index)
    {
        _data.BypassServerIndex = index;
        Persist();
    }

    public void SaveBypassServersJson(string json)
    {
        _data.BypassServersJson = json;
        Persist();
    }

    public void SaveConnectionPassword(string password)
    {
        _data.ConnectionPassword = password;
        Persist();
    }

    public void SaveVpnCredentialsFull(string uuid, string url, string status, int daysLeft, double expireAt)
    {
        _data.VpnUuid = uuid;
        _data.VpnSubscriptionUrl = url;
        _data.VpnStatusString = status;
        _data.VpnDaysLeft = daysLeft;
        _data.VpnExpireAt = expireAt;
        _data.VpnStatusLastCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _data.VpnRevokeReason = "";

        var subKey = SubscriptionChecker.ExtractSubKey(url);
        if (!string.IsNullOrEmpty(subKey))
            _data.VpnSubKey = subKey;

        Persist();
    }

    public void RevokeVpnAccess(string reason = "")
    {
        _data.VpnUuid = "";
        _data.VpnExpireAt = 0;
        _data.VpnDaysLeft = 0;
        _data.VpnStatusString = "unknown";
        // Отключение в боте — без офлайн-доступа, только онбординг (ссылка остаётся)
        if (reason is "removed" or "blocked")
            _data.ConnectionPassword = "";
        if (!string.IsNullOrEmpty(reason))
            _data.VpnRevokeReason = reason;
        Persist();
    }

    public void ClearRevokeReason()
    {
        _data.VpnRevokeReason = "";
        Persist();
    }

    public void SaveTheme(string mode, bool dynamicColor, string palette)
    {
        _data.ThemeMode = mode;
        _data.IsDynamicColor = dynamicColor;
        _data.ThemePalette = palette;
        Persist();
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(_data, JsonOptions);
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(_path))
                File.Replace(tmp, _path, destinationBackupFileName: null);
            else
                File.Move(tmp, _path);
            WriteDiag($"saved {_path}");
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(_path, json, Encoding.UTF8);
                WriteDiag($"saved direct {_path}");
            }
            catch (Exception ex2)
            {
                WriteDiag($"save failed: {ex.Message}; fallback: {ex2.Message}");
                AppLogger.Instance.ServiceErr($"Ошибка сохранения настроек: {ex.Message}");
            }
        }
        Changed?.Invoke();
    }

    private SettingsData Load()
    {
        foreach (var candidate in LegacySettingsPaths())
        {
            if (!File.Exists(candidate) || string.Equals(candidate, _path, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var legacy = ReadSettingsFile(candidate);
                if (HasAnyData(legacy))
                {
                    WriteDiag($"migrate from {candidate}");
                    try { File.Copy(candidate, _path, true); } catch { /* ignore */ }
                    return legacy;
                }
            }
            catch (Exception ex)
            {
                WriteDiag($"legacy read failed {candidate}: {ex.Message}");
            }
        }

        if (!File.Exists(_path))
        {
            WriteDiag($"new settings file {_path}");
            try { AppLogger.Instance.Service($"Настройки: новый файл {_path}"); } catch { /* ignore */ }
            return new SettingsData();
        }

        try
        {
            var data = ReadSettingsFile(_path);
            try
            {
                AppLogger.Instance.Service(
                    $"Настройки загружены: uuid={!string.IsNullOrEmpty(data.VpnUuid)}, " +
                    $"sub={!string.IsNullOrEmpty(data.VpnSubscriptionUrl)}, vk={!string.IsNullOrEmpty(data.VkCallUrl)}");
            }
            catch { /* ignore */ }
            return data;
        }
        catch (Exception ex)
        {
            WriteDiag($"load failed {_path}: {ex.Message}");
            try { AppLogger.Instance.ServiceErr($"Ошибка чтения настроек: {ex.Message}"); } catch { /* ignore */ }
            try
            {
                var fallback = ParseLegacyJson(File.ReadAllText(_path, Encoding.UTF8));
                if (HasAnyData(fallback))
                    return fallback;
            }
            catch { /* ignore */ }
            return new SettingsData();
        }
    }

    private static SettingsData ReadSettingsFile(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        try
        {
            var data = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions);
            if (data != null && HasAnyData(data))
                return data;
        }
        catch (JsonException ex)
        {
            WriteDiagStatic(path, $"deserialize: {ex.Message}");
        }

        return ParseLegacyJson(json);
    }

    private static void WriteDiagStatic(string dir, string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetDirectoryName(dir) ?? dir, "startup.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n", Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    private static SettingsData ParseLegacyJson(string json)
    {
        var data = new SettingsData();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            data.VpnUuid = ReadString(root, "vpn_uuid", "VpnUuid");
            data.VpnSubscriptionUrl = ReadString(root, "vpn_subscription_url", "VpnSubscriptionUrl");
            data.VpnSubKey = ReadString(root, "vpn_sub_key", "VpnSubKey");
            data.VkCallUrl = ReadString(root, "vk_call_url", "VkCallUrl");
            data.VkHashes = ReadString(root, "vk_hashes", "VkHashes");
            data.ConnectionPassword = ReadString(root, "connection_password", "ConnectionPassword");
            data.BypassServersJson = ReadJsonBlob(root, "bypass_servers_json", "BypassServersJson");
            data.VpnStatusString = ReadString(root, "vpn_status_string", "VpnStatusString", "unknown");
            data.VpnRevokeReason = ReadString(root, "vpn_revoke_reason", "VpnRevokeReason");
            data.Peer = ReadString(root, "peer", "Peer");
            data.ThemeMode = ReadString(root, "theme_mode", "ThemeMode", "system");
            data.ThemePalette = ReadString(root, "theme_palette", "ThemePalette", "indigo");
            if (root.TryGetProperty("workers_per_hash", out var w) || root.TryGetProperty("WorkersPerHash", out w))
                data.WorkersPerHash = w.ValueKind == JsonValueKind.Number ? w.GetInt32() : data.WorkersPerHash;
            if (root.TryGetProperty("bypass_server_index", out var b) || root.TryGetProperty("BypassServerIndex", out b))
                data.BypassServerIndex = b.ValueKind == JsonValueKind.Number ? b.GetInt32() : data.BypassServerIndex;
            if (root.TryGetProperty("vpn_days_left", out var d) || root.TryGetProperty("VpnDaysLeft", out d))
                data.VpnDaysLeft = d.ValueKind == JsonValueKind.Number ? d.GetInt32() : data.VpnDaysLeft;
            if (root.TryGetProperty("vpn_expire_at", out var e) || root.TryGetProperty("VpnExpireAt", out e))
                data.VpnExpireAt = e.ValueKind == JsonValueKind.Number ? e.GetDouble() : data.VpnExpireAt;
            if (root.TryGetProperty("is_dynamic_color", out var c) || root.TryGetProperty("IsDynamicColor", out c))
                data.IsDynamicColor = c.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return new SettingsData();
        }
        return data;
    }

    private static string ReadJsonBlob(JsonElement root, string snake, string pascal)
    {
        if (!root.TryGetProperty(snake, out var el) && !root.TryGetProperty(pascal, out el))
            return "";

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Array or JsonValueKind.Object => el.GetRawText(),
            _ => ""
        };
    }

    private static string ReadString(JsonElement root, string snake, string pascal, string fallback = "")
    {
        if (root.TryGetProperty(snake, out var el) || root.TryGetProperty(pascal, out el))
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? fallback : fallback;
        return fallback;
    }

    private static bool HasAnyData(SettingsData data) =>
        !string.IsNullOrEmpty(data.VpnUuid)
        || !string.IsNullOrEmpty(data.VpnSubscriptionUrl)
        || !string.IsNullOrEmpty(data.VkCallUrl)
        || !string.IsNullOrEmpty(data.VkHashes)
        || !string.IsNullOrEmpty(data.ConnectionPassword);

    private IEnumerable<string> LegacySettingsPaths()
    {
        yield return _path;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "FlyFrogLLC", "ByPassMe", "settings.json");

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, "AppData", "Local", "ByPassMe", "settings.json");
            yield return Path.Combine(userProfile, "AppData", "Local", "FlyFrogLLC", "ByPassMe", "settings.json");
        }
    }

    private static string ResolveSettingsDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Contains("systemprofile", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(localAppData))
        {
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(userProfile))
                localAppData = Path.Combine(userProfile, "AppData", "Local");
        }
        return Path.Combine(localAppData, "ByPassMe");
    }

    private void WriteDiag(string message)
    {
        try
        {
            var path = Path.Combine(_dir, "startup.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n", Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private sealed class SettingsData
    {
        [JsonPropertyName("peer")] public string Peer { get; set; } = "";
        [JsonPropertyName("vk_hashes")] public string VkHashes { get; set; } = "";
        [JsonPropertyName("vk_call_url")] public string VkCallUrl { get; set; } = "";
        [JsonPropertyName("workers_per_hash")] public int WorkersPerHash { get; set; } = 18;
        [JsonPropertyName("bypass_server_index")] public int BypassServerIndex { get; set; }
        [JsonPropertyName("vpn_uuid")] public string VpnUuid { get; set; } = "";
        [JsonPropertyName("vpn_subscription_url")] public string VpnSubscriptionUrl { get; set; } = "";
        [JsonPropertyName("vpn_sub_key")] public string VpnSubKey { get; set; } = "";
        [JsonPropertyName("vpn_expire_at")] public double VpnExpireAt { get; set; }
        [JsonPropertyName("vpn_status_string")] public string VpnStatusString { get; set; } = "unknown";
        [JsonPropertyName("vpn_days_left")] public int VpnDaysLeft { get; set; }
        [JsonPropertyName("vpn_revoke_reason")] public string VpnRevokeReason { get; set; } = "";
        [JsonPropertyName("bypass_servers_json")] public string BypassServersJson { get; set; } = "";
        [JsonPropertyName("connection_password")] public string ConnectionPassword { get; set; } = "";
        [JsonPropertyName("theme_mode")] public string ThemeMode { get; set; } = "system";
        [JsonPropertyName("is_dynamic_color")] public bool IsDynamicColor { get; set; }
        [JsonPropertyName("theme_palette")] public string ThemePalette { get; set; } = "indigo";
        [JsonPropertyName("vpn_status_last_check")] public double VpnStatusLastCheck { get; set; }
    }
}
