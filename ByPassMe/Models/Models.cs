namespace ByPassMe.Models;

public sealed class BypassServer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 56000;

    public string Peer => $"{Host}:{Port}";
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
