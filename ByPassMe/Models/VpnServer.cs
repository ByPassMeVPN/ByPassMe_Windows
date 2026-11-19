namespace ByPassMe.Models;

public sealed class VpnServer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public int Port { get; init; } = 443;
    public string Network { get; init; } = "grpc";
    public string Fingerprint { get; init; } = "safari";
    public string OutboundTag { get; init; } = "proxy";
    public string Sni { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Ключи Reality своего сервера (у России — отдельные). Пусто — общие из билдера.</summary>
    public string PublicKey { get; init; } = "";
    public string ShortId { get; init; } = "";

    /// <summary>Подпись протокола под именем сервера в списке.</summary>
    public string ProtocolLabel { get; init; } = "";

    /// <summary>Игровой сервер: свои параметры QUIC (brutal, короткий keepalive).</summary>
    public bool IsGaming { get; init; }

    /// <summary>Подключаться к серверу через эти прокси (пусто — напрямую).</summary>
    public IReadOnlyList<VpnProxy> Proxies { get; init; } = [];

    /// <summary>Hysteria (UDP/QUIC) через SOCKS/HTTP-прокси не ходит — для неё прокси не применяются.</summary>
    public IReadOnlyList<VpnProxy> ActiveProxies =>
        string.Equals(Network, "hysteria", StringComparison.OrdinalIgnoreCase) ? Array.Empty<VpnProxy>() : Proxies;
}

/// <summary>
/// Входной прокси перед сервером (SOCKS5 или HTTP CONNECT). Список, адреса и
/// логины/пароли меняются на hub.mos.ru — в приложении ничего не зашито.
/// </summary>
public sealed class VpnProxy
{
    public string Id { get; init; } = "";
    /// <summary>"socks" или "http".</summary>
    public string Type { get; init; } = "socks";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string User { get; init; } = "";
    public string Pass { get; init; } = "";

    public bool HasAuth => User.Length > 0 || Pass.Length > 0;
}
