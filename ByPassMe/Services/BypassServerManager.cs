using System.Net.Http;
using System.Text.Json;
using ByPassMe.Models;

namespace ByPassMe.Services;

public sealed class BypassServerManager
{
    public static BypassServerManager Instance { get; } = new();

    private const string HubApiBase = "https://hub.mos.ru/api/v4/projects";
    private const string HubProject = "dzonsonandrej706%2Fdzonson";
    private const string HubFile = "bypass-servers.json";
    private const string HubBranch = "main";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public IReadOnlyList<BypassServer> Servers => _servers;
    private List<BypassServer> _servers = [];

    public event Action? Changed;

    private static string ResolvedHubToken =>
        HubTokenClass.Mos.Trim() is var t && !string.IsNullOrEmpty(t) && t != "@HUB_MOS_TOKEN@"
            ? t
            : Environment.GetEnvironmentVariable("HUB_MOS_TOKEN") ?? "";

    public void LoadCached()
    {
        if (_servers.Count > 0) return;
        var json = SettingsStore.Instance.BypassServersJson;
        if (string.IsNullOrEmpty(json)) return;
        var parsed = ParseServersJson(json);
        if (parsed.Count > 0)
        {
            _servers = parsed;
            Changed?.Invoke();
        }
    }

    public async Task<FetchResult> FetchServersAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ResolvedHubToken) || ResolvedHubToken == "@HUB_MOS_TOKEN@")
        {
            AppLogger.Instance.ServiceDbg("Серверы: токен не задан — используется кэш");
            LoadCached();
            return _servers.Count == 0 ? FetchResult.MissingHubToken : FetchResult.Success;
        }

        if (await FetchFromHubAsync(ct))
            return FetchResult.Success;

        LoadCached();
        return _servers.Count == 0 ? FetchResult.NetworkError : FetchResult.Success;
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
                AppLogger.Instance.ServiceDbg($"Серверы: HTTP {(int)response.StatusCode}");
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("servers", out var serversEl))
            {
                AppLogger.Instance.ServiceDbg("Серверы: неверный JSON");
                return false;
            }

            var list = ParseServersArray(serversEl);
            if (list.Count == 0) return false;

            _servers = list;
            SettingsStore.Instance.SaveBypassServersJson(serversEl.GetRawText());
            AppLogger.Instance.ServiceDbg($"Серверы обновлены: {list.Count}");
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.ServiceDbg($"Серверы: {ex.Message}");
            return false;
        }
    }

    public void RefreshInBackground() => _ = FetchServersAsync();

    public void Clear()
    {
        _servers = [];
        Changed?.Invoke();
    }

    public int DefaultServerIndex(IReadOnlyList<BypassServer> list)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == "nl") return i;
        return 0;
    }

    private static List<BypassServer> ParseServersJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return ParseServersArray(root);
            if (root.TryGetProperty("servers", out var servers))
                return ParseServersArray(servers);
        }
        catch { /* ignore */ }
        return [];
    }

    private static List<BypassServer> ParseServersArray(JsonElement arr)
    {
        var result = new List<BypassServer>();
        if (arr.ValueKind != JsonValueKind.Array) return result;

        foreach (var s in arr.EnumerateArray())
        {
            var host = s.TryGetProperty("host", out var h) ? h.GetString()?.Trim() ?? "" : "";
            if (string.IsNullOrEmpty(host)) continue;

            var port = 56000;
            if (s.TryGetProperty("port", out var p))
            {
                port = p.ValueKind switch
                {
                    JsonValueKind.Number => p.GetInt32(),
                    JsonValueKind.String => int.TryParse(p.GetString(), out var pi) ? pi : 56000,
                    _ => 56000
                };
            }

            result.Add(new BypassServer
            {
                Id = s.TryGetProperty("id", out var id) ? id.GetString() ?? host : host,
                Name = s.TryGetProperty("name", out var n) ? n.GetString() ?? host : host,
                Host = host,
                Port = port
            });
        }
        return result;
    }
}

// Alias to avoid naming conflict
file static class HubTokenClass
{
    public static string Mos => global::ByPassMe.HubToken.Mos;
}
