using System.Text.Json.Nodes;

namespace ByPassMe.Helpers;

/// <summary>
/// VPN: geosite РФ + .ru/.su/.рф → direct; остальное — в туннель (без geoip-ru).
/// </summary>
public static class RuSplitRouting
{
    /// <summary>Зоны .ru / .su / .рф (punycode).</summary>
    public static readonly string[] TldSuffixes = ["ru", "su", "xn--p1ai"];

    public static readonly (string File, string Tag)[] BundledRuleSets =
    [
        ("geosite-category-ru.srs", "geosite-category-ru"),
        ("geosite-category-gov-ru.srs", "geosite-category-gov-ru"),
    ];

    public static JsonArray TldSuffixJson()
    {
        var a = new JsonArray();
        foreach (var tld in TldSuffixes)
            a.Add(tld);
        return a;
    }

    /// <summary>xray: geosite + regexp по зонам .ru/.su/.рф.</summary>
    public static JsonArray XrayDirectDomains()
    {
        var a = new JsonArray
        {
            "geosite:category-ru",
            "geosite:category-gov-ru",
        };
        foreach (var tld in TldSuffixes)
            a.Add($"regexp:\\.{tld}$");
        return a;
    }
}
