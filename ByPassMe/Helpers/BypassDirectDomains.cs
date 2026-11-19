using System.IO;
using System.Text.Json;

namespace ByPassMe.Helpers;

/// <summary>
/// Обход Б/С: напрямую только домены из белого списка (конфиг стороннего VPN).
/// </summary>
public static class BypassDirectDomains
{
    private static readonly Lazy<string[]> Domains = new(Load);

    public static IReadOnlyList<string> All => Domains.Value;

    private static string[] Load()
    {
        foreach (var rel in new[] { Path.Combine("Assets", "bypass-direct-domains.json"), "bypass-direct-domains.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, rel);
            if (!File.Exists(path))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    continue;

                var list = new List<string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String)
                        continue;
                    var d = el.GetString()?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(d))
                        list.Add(d);
                }

                return list.Distinct(StringComparer.Ordinal).ToArray();
            }
            catch
            {
                return [];
            }
        }

        return [];
    }
}
