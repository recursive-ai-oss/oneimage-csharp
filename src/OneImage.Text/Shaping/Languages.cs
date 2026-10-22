namespace OneImage.Text.Shaping;

/// <summary>Maps BCP 47 language tags to OpenType language system tags (the registry in the OpenType specification) for the languages of the simple scripts.</summary>
internal static class Languages
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sq"] = "SQI", ["eu"] = "EUQ", ["be"] = "BEL", ["bs"] = "BOS", ["bg"] = "BGR", ["ca"] = "CAT", ["hr"] = "HRV",
        ["cs"] = "CSY", ["da"] = "DAN", ["nl"] = "NLD", ["en"] = "ENG", ["et"] = "ETI", ["fi"] = "FIN", ["fr"] = "FRA",
        ["gl"] = "GAL", ["de"] = "DEU", ["el"] = "ELL", ["hu"] = "HUN", ["is"] = "ISL", ["ga"] = "IRI", ["it"] = "ITA",
        ["kk"] = "KAZ", ["lv"] = "LVI", ["lt"] = "LTH", ["lb"] = "LTZ", ["mk"] = "MKD", ["mt"] = "MTS", ["mn"] = "MNG",
        ["nb"] = "NOR", ["no"] = "NOR", ["nn"] = "NYN", ["pl"] = "PLK", ["pt"] = "PTG", ["ro"] = "ROM", ["ru"] = "RUS",
        ["sr"] = "SRB", ["sk"] = "SKY", ["sl"] = "SLV", ["es"] = "ESP", ["sv"] = "SVE", ["tr"] = "TRK", ["az"] = "AZE",
        ["uk"] = "UKR", ["vi"] = "VIT", ["cy"] = "WEL", ["gd"] = "GAE", ["mi"] = "MAO", ["sm"] = "SMO", ["haw"] = "HAW",
        ["nv"] = "NAV", ["ro-md"] = "MOL", ["tt"] = "TAT", ["ky"] = "KIR", ["tg"] = "TAJ", ["cv"] = "CHU",
    };

    /// <summary>The language system tag as the four bytes stored in a font (space padded); 0 for no language or one without a tag here.</summary>
    public static uint ToTag(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return 0;
        }

        string s = language.Trim();
        if (s.Length is 3 or 4 && s.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            return Pack(s);
        }

        string[] parts = s.Split('-', '_');
        if (parts.Length > 1 && Map.TryGetValue(parts[0] + "-" + parts[^1], out string? regional))
        {
            return Pack(regional);
        }

        return Map.TryGetValue(parts[0], out string? tag) ? Pack(tag) : 0;
    }

    private static uint Pack(string tag)
    {
        string padded = tag.PadRight(4);
        return ((uint)padded[0] << 24) | ((uint)padded[1] << 16) | ((uint)padded[2] << 8) | padded[3];
    }
}
