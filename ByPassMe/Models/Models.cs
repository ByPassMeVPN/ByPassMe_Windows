namespace ByPassMe.Models;

public sealed class BypassServer
{
    /// <summary>Порт режима rawtun на сервере (-listen-raw). Старый WireGuard-порт 56000 остаётся для прежних приложений.</summary>
    public const int DefaultRawPort = 56003;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 56000;
    public int RawPort { get; init; } = DefaultRawPort;

    public string Peer => $"{Host}:{Port}";

    /// <summary>Запасные входные адреса страны (alt_hosts, необязательное поле).</summary>
    public IReadOnlyList<string> AltHosts { get; init; } = Array.Empty<string>();

    /// <summary>Сервер понимает «голосовой» RTP-заголовок (voice, необязательное поле).</summary>
    public bool Voice { get; init; }

    /// <summary>Адрес для приложения: rawtun-порт.</summary>
    public string RawPeer => $"{Host}:{RawPort}";

    /// <summary>
    /// -peer для Go-клиента: основной и запасные адреса через запятую —
    /// клиент сам переключится, если основной не отвечает.
    /// </summary>
    public string RawPeerList => AltHosts.Count == 0
        ? RawPeer
        : string.Join(",", new[] { RawPeer }.Concat(AltHosts.Select(a => $"{a}:{RawPort}")));
}

public enum LogSource
{
    Vpn,
    Bypass,
    Service,
    System
}

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public sealed class AppLogEntry
{
    public long Id { get; init; }
    public string Timestamp { get; init; } = "";
    public LogSource Source { get; init; }
    public LogLevel Level { get; init; }
    public string Message { get; init; } = "";

    public string SourceLabel => Source switch
    {
        LogSource.Vpn => "VPN",
        LogSource.Bypass => "Обход",
        LogSource.Service => "Сервис",
        LogSource.System => "Система",
        _ => "?"
    };
}

public enum SubscriptionResult
{
    Success,
    DeviceLimitExceeded,
    DeviceBlocked,
    DeviceRemoved,
    Error
}

public enum FetchResult
{
    Success,
    NoSubscription,
    NoAccess,
    NotFound,
    EmptyList,
    MissingHubToken,
    NetworkError
}
