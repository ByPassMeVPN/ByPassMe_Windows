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
            {
                var fromEmoji = EmojiToIso(first);
                if (!string.IsNullOrEmpty(fromEmoji))
                    return fromEmoji;
            }
            if (first.Length == 2 && char.IsAsciiLetter(first[0]) && char.IsAsciiLetter(first[1]))
                return first.ToUpperInvariant();
        }

        var fromId = IdToIso(serverId);
        if (!string.IsNullOrEmpty(fromId))
            return fromId;

        return NameToIso(serverName);
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
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            var r1 = char.ConvertToUtf32(text, 0);
            return r1 is >= 0x1F1E6 and <= 0x1F1FF;
        }
        catch
        {
            return false;
        }
    }

    private static string EmojiToIso(string emoji)
    {
        if (emoji.Length < 2)
            return "";

        try
        {
            var r1 = char.ConvertToUtf32(emoji, 0);
            if (r1 < 0x1F1E6 || r1 > 0x1F1FF)
                return "";
            var idx2 = char.IsHighSurrogate(emoji[0]) ? 2 : 1;
            if (idx2 >= emoji.Length)
                return "";
            var r2 = char.ConvertToUtf32(emoji, idx2);
            if (r2 < 0x1F1E6 || r2 > 0x1F1FF)
                return "";
            return $"{(char)('A' + (r1 - 0x1F1E6))}{(char)('A' + (r2 - 0x1F1E6))}";
        }
        catch
        {
            return "";
        }
    }

    private static string IdToIso(string id)
    {
        var key = System.Text.RegularExpressions.Regex.Replace(id.Trim().ToLowerInvariant(), @"\d+$", "");
        if (key.Length == 2 && key.All(char.IsAsciiLetter))
            return key.ToUpperInvariant();

        return key switch
        {
            "nl" or "netherlands" or "holland" => "NL",
            "us" or "usa" or "america" or "ny" or "newyork" or "new-york" => "US",
            "de" or "germany" or "deutschland" => "DE",
            "gb" or "uk" or "london" or "britain" or "england" => "GB",
            "fr" or "france" => "FR",
            "fi" or "finland" => "FI",
            "se" or "sweden" => "SE",
            "ro" or "romania" => "RO",
            "pl" or "poland" => "PL",
            "tr" or "turkey" or "turkiye" => "TR",
            "jp" or "japan" => "JP",
            "sg" or "singapore" => "SG",
            "ru" or "russia" => "RU",
            "ua" or "ukraine" => "UA",
            "kz" or "kazakhstan" => "KZ",
            "cz" or "czechia" or "czech" => "CZ",
            "at" or "austria" => "AT",
            "ch" or "switzerland" => "CH",
            "it" or "italy" => "IT",
            "es" or "spain" => "ES",
            "pt" or "portugal" => "PT",
            "be" or "belgium" => "BE",
            "ie" or "ireland" => "IE",
            "no" or "norway" => "NO",
            "dk" or "denmark" => "DK",
            "ca" or "canada" => "CA",
            "au" or "australia" => "AU",
            "br" or "brazil" => "BR",
            "in" or "india" => "IN",
            "cn" or "china" => "CN",
            "kr" or "korea" or "southkorea" => "KR",
            "hk" or "hongkong" => "HK",
            "ae" or "uae" or "dubai" => "AE",
            "il" or "israel" => "IL",
            "md" or "moldova" => "MD",
            "bg" or "bulgaria" => "BG",
            "hu" or "hungary" => "HU",
            "rs" or "serbia" => "RS",
            "hr" or "croatia" => "HR",
            "sk" or "slovakia" => "SK",
            "si" or "slovenia" => "SI",
            "lt" or "lithuania" => "LT",
            "lv" or "latvia" => "LV",
            "ee" or "estonia" => "EE",
            "ge" or "georgia" => "GE",
            "am" or "armenia" => "AM",
            "az" or "azerbaijan" => "AZ",
            "by" or "belarus" => "BY",
            "mx" or "mexico" => "MX",
            "ar" or "argentina" => "AR",
            "cl" or "chile" => "CL",
            "za" or "southafrica" => "ZA",
            "eg" or "egypt" => "EG",
            "th" or "thailand" => "TH",
            "vn" or "vietnam" => "VN",
            "id" or "indonesia" => "ID",
            "my" or "malaysia" => "MY",
            "ph" or "philippines" => "PH",
            "nz" or "newzealand" => "NZ",
            "is" or "iceland" => "IS",
            "lu" or "luxembourg" => "LU",
            "mt" or "malta" => "MT",
            "cy" or "cyprus" => "CY",
            "gr" or "greece" => "GR",
            "tw" or "taiwan" => "TW",
            "mo" or "macau" => "MO",
            _ => ""
        };
    }

    private static string NameToIso(string serverName)
    {
        var name = serverName.ToLowerInvariant();
        // strip leading emoji / iso token
        var parts = serverName.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
            name = parts[1].ToLowerInvariant();

        if (name.Contains("нидерланд") || name.Contains("netherlands") || name.Contains("holland")) return "NL";
        if (name.Contains("герман") || name.Contains("german")) return "DE";
        if (name.Contains("финлянд") || name.Contains("finland")) return "FI";
        if (name.Contains("швец") || name.Contains("sweden")) return "SE";
        if (name.Contains("румын") || name.Contains("romania")) return "RO";
        if (name.Contains("лондон") || name.Contains("britain") || name.Contains("london") || name.Contains("великобритан")) return "GB";
        if (name.Contains("нью-йорк") || name.Contains("нью йорк") || name.Contains("new york") || name.Contains("америк") || name.Contains("сша")) return "US";
        if (name.Contains("франц") || name.Contains("france")) return "FR";
        if (name.Contains("польш") || name.Contains("poland")) return "PL";
        if (name.Contains("турц") || name.Contains("turkey") || name.Contains("türkiye")) return "TR";
        if (name.Contains("япон") || name.Contains("japan")) return "JP";
        if (name.Contains("сингапур") || name.Contains("singapore")) return "SG";
        if (name.Contains("росси") || name.Contains("russia")) return "RU";
        if (name.Contains("украин") || name.Contains("ukraine")) return "UA";
        if (name.Contains("казах") || name.Contains("kazakh")) return "KZ";
        if (name.Contains("испан") || name.Contains("spain")) return "ES";
        if (name.Contains("итал") || name.Contains("italy")) return "IT";
        if (name.Contains("канад") || name.Contains("canada")) return "CA";
        if (name.Contains("австрал") || name.Contains("australia")) return "AU";
        if (name.Contains("бразил") || name.Contains("brazil")) return "BR";
        if (name.Contains("инди") || name.Contains("india")) return "IN";
        if (name.Contains("китай") || name.Contains("china")) return "CN";
        if (name.Contains("коре") || name.Contains("korea")) return "KR";
        if (name.Contains("оаэ") || name.Contains("дубай") || name.Contains("emirates")) return "AE";
        if (name.Contains("израил") || name.Contains("israel")) return "IL";
        if (name.Contains("молдав") || name.Contains("moldova")) return "MD";
        if (name.Contains("болгар") || name.Contains("bulgaria")) return "BG";
        if (name.Contains("венгр") || name.Contains("hungary")) return "HU";
        if (name.Contains("серб") || name.Contains("serbia")) return "RS";
        if (name.Contains("хорват") || name.Contains("croatia")) return "HR";
        if (name.Contains("чехи") || name.Contains("czech")) return "CZ";
        if (name.Contains("австр") || name.Contains("austria")) return "AT";
        if (name.Contains("швейцар") || name.Contains("switzerland")) return "CH";
        if (name.Contains("норвег") || name.Contains("norway")) return "NO";
        if (name.Contains("дат") || name.Contains("denmark")) return "DK";
        if (name.Contains("ирланд") || name.Contains("ireland")) return "IE";
        if (name.Contains("бельг") || name.Contains("belgium")) return "BE";
        if (name.Contains("португал") || name.Contains("portugal")) return "PT";
        if (name.Contains("грец") || name.Contains("greece")) return "GR";
        if (name.Contains("литв") || name.Contains("lithuania")) return "LT";
        if (name.Contains("латви") || name.Contains("latvia")) return "LV";
        if (name.Contains("эстон") || name.Contains("estonia")) return "EE";
        return "";
    }
}
