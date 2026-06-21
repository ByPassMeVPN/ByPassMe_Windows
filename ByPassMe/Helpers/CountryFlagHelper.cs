using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ByPassMe.Helpers;

public static class CountryFlagHelper
{
    public static ImageSource ResolveFlagImage(string serverId, string serverName)
    {
        var iso = ResolveIsoCode(serverId, serverName);
        if (!string.IsNullOrEmpty(iso))
        {
            var img = TryLoad($"/Assets/Flags/{iso.ToLowerInvariant()}.png");
            if (img != null)
                return img;
        }

        return TryLoad("/Assets/Flags/globe.png")
               ?? new BitmapImage();
    }

    public static string ResolveCountryName(string serverName)
    {
        var parts = serverName.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return serverName;

        var first = parts[0];
        if (IsFlagEmoji(first) || (first.Length == 2 && char.IsAsciiLetter(first[0])))
            return parts[1];

        return serverName;
    }

    public static string ResolveIsoCode(string serverId, string serverName)
    {
        var parts = serverName.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length > 0)
        {
            var first = parts[0];
            if (IsFlagEmoji(first))
                return EmojiToIso(first);
            if (first.Length == 2 && char.IsAsciiLetter(first[0]) && char.IsAsciiLetter(first[1]))
                return first.ToUpperInvariant();
        }

        return IdToIso(serverId);
    }

    private static ImageSource? TryLoad(string packPath)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,{packPath}", UriKind.Absolute);
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFlagEmoji(string text)
    {
        if (text.Length < 2)
            return false;
        foreach (var ch in text)
        {
            if (char.IsSurrogate(ch))
                return true;
        }
        return false;
    }

    private static string EmojiToIso(string emoji)
    {
        if (emoji.Length < 2)
            return "";

        var chars = emoji.Where(c => !char.IsSurrogate(c) || char.IsHighSurrogate(c)).ToArray();
        if (chars.Length < 2)
            return "";

        try
        {
            var r1 = char.ConvertToUtf32(emoji, 0);
            if (r1 < 0x1F1E6 || r1 > 0x1F1FF + 25)
                return "";
            var r2 = char.ConvertToUtf32(emoji, char.IsHighSurrogate(emoji[0]) ? 2 : 1);
            if (r2 < 0x1F1E6 || r2 > 0x1F1FF + 25)
                return "";
            return $"{(char)('A' + (r1 - 0x1F1E6))}{(char)('A' + (r2 - 0x1F1E6))}";
        }
        catch
        {
            return "";
        }
    }

    private static string IdToIso(string id) => id.Trim().ToLowerInvariant() switch
    {
        "nl" or "netherlands" => "NL",
        "us" or "usa" => "US",
        "de" or "germany" => "DE",
        "gb" or "uk" => "GB",
        "fr" or "france" => "FR",
        "fi" or "finland" => "FI",
        "se" or "sweden" => "SE",
        "pl" or "poland" => "PL",
        "tr" or "turkey" => "TR",
        "jp" or "japan" => "JP",
        "sg" or "singapore" => "SG",
        _ => id.Length == 2 ? id.ToUpperInvariant() : ""
    };
}
