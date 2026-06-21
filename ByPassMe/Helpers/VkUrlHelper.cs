namespace ByPassMe.Helpers;

public static class VkUrlHelper
{
    private static readonly string[] Prefixes =
    [
        "https://vk.com/call/join/",
        "http://vk.com/call/join/",
        "https://vk.ru/call/join/",
        "http://vk.ru/call/join/",
        "vk.com/call/join/",
        "vk.ru/call/join/"
    ];

    public static string StripVkUrl(string input)
    {
        var s = input.Trim();
        foreach (var prefix in Prefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        var qIdx = s.IndexOf('?');
        if (qIdx >= 0) s = s[..qIdx];
        var hIdx = s.IndexOf('#');
        if (hIdx >= 0) s = s[..hIdx];
        return s.TrimEnd('/');
    }

    public static bool IsValidVkHash(string hash) => hash.Length >= 16;

    public static int RoundToGroup(int value, int step = 9, int min = 18, int max = 36)
    {
        var rounded = (int)Math.Round(value / (double)step) * step;
        return Math.Clamp(rounded, min, max);
    }

    public static string DayWord(int days)
    {
        var mod10 = days % 10;
        var mod100 = days % 100;
        if (mod10 == 1 && mod100 != 11) return "день";
        if (mod10 is >= 2 and <= 4 && !(mod100 >= 12 && mod100 <= 14)) return "дня";
        return "дней";
    }
}
