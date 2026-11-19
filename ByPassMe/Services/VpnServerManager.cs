using System.Net.Http;
using System.Text.Json;
using ByPassMe.Models;

namespace ByPassMe.Services;

public sealed class VpnServerManager
{
    public static VpnServerManager Instance { get; } = new();

    private const string HubApiBase = "https://hub.mos.ru/api/v4/projects";
    private const string HubProject = "dzonsonandrej706%2Fdzonson";
    private const string HubFile = "vpn-servers.json";
    private const string HubBranch = "main";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly object _fetchLock = new();
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private Task<FetchResult>? _inFlight;

    public IReadOnlyList<VpnServer> Servers => _servers;
    private List<VpnServer> _servers = [];

    public event Action? Changed;

    private static string ResolvedHubToken =>
        HubTokenClass.Mos.Trim() is var t && !string.IsNullOrEmpty(t)
            ? t
            : Environment.GetEnvironmentVariable("HUB_MOS_TOKEN") ?? "";

    public void LoadCached()
    {
        if (_servers.Count > 0) return;

        // Список серверов — только с mos hub (и его кэш с прошлого запуска).
        var json = SettingsStore.Instance.VpnServersJson;
        if (!string.IsNullOrEmpty(json))
            ApplyServersJson(json, log: false);
    }

    public Task<FetchResult> FetchServersAsync(CancellationToken ct = default)
    {
        lock (_fetchLock)
        {
            if (_inFlight != null)
                return _inFlight;
            if (DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromSeconds(20) && _servers.Count > 0)
                return Task.FromResult(FetchResult.Success);

            _inFlight = FetchServersCoreAsync(ct);
            return _inFlight;
        }
    }

    private async Task<FetchResult> FetchServersCoreAsync(CancellationToken ct)
    {
        try
        {
            if (VpnManager.Instance.IsRunning || VpnManager.Instance.IsConnecting)
            {
                LoadCached();
                return _servers.Count == 0 ? FetchResult.NetworkError : FetchResult.Success;
            }

            if (string.IsNullOrEmpty(ResolvedHubToken))
            {
                LoadCached();
                return _servers.Count == 0 ? FetchResult.MissingHubToken : FetchResult.Success;
            }

            if (await FetchFromHubAsync(ct))
            {
                _lastFetchUtc = DateTime.UtcNow;
                return FetchResult.Success;
            }

            LoadCached();
            return _servers.Count == 0 ? FetchResult.NetworkError : FetchResult.Success;
        }
        finally
        {
            lock (_fetchLock) _inFlight = null;
        }
    }

    public void RefreshInBackground() => _ = FetchServersAsync();

    public void Clear()
    {
        _servers = [];
        Changed?.Invoke();
    }

    public int DefaultServerIndex(IReadOnlyList<VpnServer> list)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == "nl") return i;
        return 0;
    }

    private async Task<bool> FetchFromHubAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{HubApiBase}/{HubProject}/repository/files/{HubFile}/raw?ref={HubBranch}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", ResolvedHubToken);
            request.Headers.TryAddWithoutValidation("User-Agent", "ByPassMe/2.0 Windows");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Instance.ServiceDbg($"VPN серверы: HTTP {(int)response.StatusCode}");
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!ApplyServersJson(body, log: true)) return false;

            SettingsStore.Instance.SaveVpnServersJson(body);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.ServiceDbg($"VPN серверы: {ex.Message}");
            return false;
        }
    }

    private bool ApplyServersJson(string body, bool log)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement arr = root.ValueKind == JsonValueKind.Array
                ? root
                : root.GetProperty("servers");

            List<VpnProxy> pool = root.ValueKind == JsonValueKind.Object ? ParseProxyPool(root) : [];
            var list = ParseServersArray(arr, pool);
            if (list.Count == 0) return false;
            _servers = list;
            if (log)
                AppLogger.Instance.Service($"VPN серверы обновлены: {list.Count}");
            Changed?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Str(JsonElement o, string name, string alt = "")
    {
        if (o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        if (alt.Length > 0 && o.TryGetProperty(alt, out var a) && a.ValueKind == JsonValueKind.String)
            return a.GetString() ?? "";
        return "";
    }

    private static int Int(JsonElement o, string name) =>
        !o.TryGetProperty(name, out var v) ? 0 : v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(v.GetString(), out var i) ? i : 0,
            _ => 0,
        };

    /// <summary>Тип прокси из списка; неизвестный — null (такой прокси пропускается).</summary>
    private static string? ProxyType(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "" or "socks" or "socks5" or "socks5h" => "socks",
        "http" => "http",
        _ => null,
    };

    private static VpnProxy? ParseProxy(JsonElement o, string fallbackId)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        if (o.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True) return null;
        var type = ProxyType(Str(o, "type"));
        if (type == null) return null;
        var host = Str(o, "host", "address").Trim();
        var port = Int(o, "port");
        if (host.Length == 0 || port is <= 0 or > 65535) return null;
        var id = Str(o, "id").Trim();
        return new VpnProxy
        {
            Id = id.Length > 0 ? id : fallbackId,
            Type = type,
            Host = host,
            Port = port,
            User = Str(o, "user", "username"),
            Pass = Str(o, "pass", "password"),
        };
    }

    /// <summary>Общий список прокси: корневой "proxies" в vpn-servers.json.</summary>
    private static List<VpnProxy> ParseProxyPool(JsonElement root)
    {
        var pool = new List<VpnProxy>();
        if (!root.TryGetProperty("proxies", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return pool;
        var i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            var p = ParseProxy(e, $"p{i++}");
            if (p != null) pool.Add(p);
        }
        return pool;
    }

    /// <summary>
    /// Поле "proxies" у сервера: "all" / true — все из общего списка; массив id —
    /// только эти; в массиве можно описать прокси и целиком (объектом).
    /// Нет поля — сервер напрямую, как раньше.
    /// </summary>
    private static List<VpnProxy> ServerProxies(JsonElement s, List<VpnProxy> pool)
    {
        if (!s.TryGetProperty("proxies", out var v)) return [];
        switch (v.ValueKind)
        {
            case JsonValueKind.True:
                return [.. pool];
            case JsonValueKind.String:
                var str = v.GetString() ?? "";
                return string.Equals(str, "all", StringComparison.OrdinalIgnoreCase)
                    ? [.. pool]
                    : pool.Where(p => p.Id == str).ToList();
            case JsonValueKind.Array:
                var list = new List<VpnProxy>();
                var i = 0;
                foreach (var e in v.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String)
                        list.AddRange(pool.Where(p => p.Id == e.GetString()));
                    else if (ParseProxy(e, $"s{i}") is { } inline)
                        list.Add(inline);
                    i++;
                }
                return list.DistinctBy(p => $"{p.Type}|{p.Host}|{p.Port}|{p.User}").ToList();
            default:
                return [];
        }
    }

    /// <summary>
    /// Прокси (их число, адреса, логины) меняются на hub.mos.ru в любой момент —
    /// перед подключением к серверу с прокси берём свежий список (до 4 с, иначе — кэш).
    /// Возвращает тот же сервер (по id) из нового списка.
    /// </summary>
    public async Task<VpnServer> RefreshForConnectAsync(VpnServer server, CancellationToken ct = default)
    {
        if (server.Proxies.Count == 0 || string.IsNullOrEmpty(ResolvedHubToken))
            return server;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            if (await FetchFromHubAsync(cts.Token))
                _lastFetchUtc = DateTime.UtcNow;
        }
        catch
        {
            // кэш
        }
        return _servers.FirstOrDefault(s => s.Id == server.Id) ?? server;
    }

    private static List<VpnServer> ParseServersArray(JsonElement arr, List<VpnProxy> pool)
    {
        var result = new List<VpnServer>();
        if (arr.ValueKind != JsonValueKind.Array) return result;

        foreach (var s in arr.EnumerateArray())
        {
            var address = s.TryGetProperty("address", out var a) ? a.GetString()?.Trim() ?? "" : "";
            if (string.IsNullOrEmpty(address)) continue;

            var port = 443;
            if (s.TryGetProperty("port", out var p))
            {
                port = p.ValueKind switch
                {
                    JsonValueKind.Number => p.GetInt32(),
                    JsonValueKind.String => int.TryParse(p.GetString(), out var pi) ? pi : 443,
                    _ => 443,
                };
            }

            result.Add(new VpnServer
            {
                Id = s.TryGetProperty("id", out var id) ? id.GetString() ?? address : address,
                Name = s.TryGetProperty("name", out var n) ? n.GetString() ?? address : address,
                Address = address,
                Port = port,
                Network = s.TryGetProperty("network", out var net) ? net.GetString() ?? "grpc" : "grpc",
                Fingerprint = s.TryGetProperty("fingerprint", out var fp) ? fp.GetString() ?? "safari" : "safari",
                OutboundTag = s.TryGetProperty("outboundTag", out var tag) ? tag.GetString() ?? "proxy" : "proxy",
                Sni = s.TryGetProperty("sni", out var sni) ? sni.GetString()?.Trim() ?? "" : "",
                Path = s.TryGetProperty("path", out var path) ? path.GetString()?.Trim() ?? "" : "",
                PublicKey = s.TryGetProperty("publicKey", out var pk) ? pk.GetString()?.Trim() ?? "" : "",
                ShortId = s.TryGetProperty("shortId", out var sid) ? sid.GetString()?.Trim() ?? "" : "",
                ProtocolLabel = s.TryGetProperty("protocolLabel", out var pl) ? pl.GetString()?.Trim() ?? "" : "",
                IsGaming = s.TryGetProperty("gaming", out var g) && g.ValueKind == JsonValueKind.True,
                Proxies = ServerProxies(s, pool),
            });
        }

        return result;
    }
}

file static class HubTokenClass
{
    public static string Mos => global::ByPassMe.HubToken.Mos;
}
