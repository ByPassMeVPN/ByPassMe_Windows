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

    /// <summary>Адрес для приложения: rawtun-порт.</summary>
    public string RawPeer => $"{Host}:{RawPort}";
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
