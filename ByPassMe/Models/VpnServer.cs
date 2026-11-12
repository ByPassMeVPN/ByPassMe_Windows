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
}
