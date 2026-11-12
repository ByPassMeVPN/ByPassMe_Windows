namespace ByPassMe;

/// <summary>
/// Токен hub.mos.ru — XOR-байты (как Android HubToken.kt).
/// Plaintext glpat не хранится в исходниках; Enc подставляется при сборке.
/// </summary>
internal static class HubToken
{
    private const byte Key = 0x5C;

    // Placeholder — build-windows.ps1 перезаписывает Enc при CI/локальной сборке.
    private static readonly byte[] Enc = [];

    private static string? _cached;

    internal static string Mos
    {
        get
        {
            if (_cached != null) return _cached;
            _cached = Decode(Enc);
            return _cached;
        }
    }

    private static string Decode(byte[] enc)
    {
        if (enc.Length == 0) return "";
        var bytes = new byte[enc.Length];
        for (var i = 0; i < enc.Length; i++)
            bytes[i] = (byte)(enc[i] ^ Key ^ (i & 0xFF));
        try { return System.Text.Encoding.UTF8.GetString(bytes); }
        catch { return ""; }
    }
}
