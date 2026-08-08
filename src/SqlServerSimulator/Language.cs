namespace SqlServerSimulator;

/// <summary>
/// One row of SQL Server's installed-language table — the set <c>SET LANGUAGE</c>
/// resolves against, <c>@@LANGUAGE</c> / <c>@@LANGID</c> report, and
/// <c>sys.syslanguages</c> projects.
/// </summary>
/// <remarks>
/// The 34 rows are a stock SQL Server 2025 instance's, captured verbatim
/// (2026-08-08). <see cref="DateFirst"/> is the load-bearing column: a
/// successful <c>SET LANGUAGE</c> carries it into the session's
/// <c>@@DATEFIRST</c>. <see cref="DateFormat"/> is projected but drives
/// nothing, since <c>SET DATEFORMAT</c> itself parses-and-discards.
/// </remarks>
internal sealed class Language(short langId, string name, string alias, string dateFormat, byte dateFirst, int lcid, short msgLangId)
{
    public readonly short LangId = langId;
    public readonly string Name = name;
    public readonly string Alias = alias;
    public readonly string DateFormat = dateFormat;
    public readonly byte DateFirst = dateFirst;
    public readonly int Lcid = lcid;
    public readonly short MsgLangId = msgLangId;

    /// <summary>
    /// Every installed language, in <c>langid</c> order — which is also
    /// <c>sys.syslanguages</c>'s own order.
    /// </summary>
    public static readonly Language[] All =
    [
        new(0, "us_english", "English", "mdy", 7, 1033, 1033),
        new(1, "Deutsch", "German", "dmy", 1, 1031, 1031),
        new(2, "Français", "French", "dmy", 1, 1036, 1036),
        new(3, "日本語", "Japanese", "ymd", 7, 1041, 1041),
        new(4, "Dansk", "Danish", "dmy", 1, 1030, 1030),
        new(5, "Español", "Spanish", "dmy", 1, 3082, 3082),
        new(6, "Italiano", "Italian", "dmy", 1, 1040, 1040),
        new(7, "Nederlands", "Dutch", "dmy", 1, 1043, 1043),
        new(8, "Norsk", "Norwegian", "dmy", 1, 2068, 2068),
        new(9, "Português", "Portuguese", "dmy", 7, 2070, 2070),
        new(10, "Suomi", "Finnish", "dmy", 1, 1035, 1035),
        new(11, "Svenska", "Swedish", "ymd", 1, 1053, 1053),
        new(12, "čeština", "Czech", "dmy", 1, 1029, 1029),
        new(13, "magyar", "Hungarian", "ymd", 1, 1038, 1038),
        new(14, "polski", "Polish", "dmy", 1, 1045, 1045),
        new(15, "română", "Romanian", "dmy", 1, 1048, 1048),
        new(16, "hrvatski", "Croatian", "ymd", 1, 1050, 1050),
        new(17, "slovenčina", "Slovak", "dmy", 1, 1051, 1051),
        new(18, "slovenski", "Slovenian", "dmy", 1, 1060, 1060),
        new(19, "ελληνικά", "Greek", "dmy", 1, 1032, 1032),
        new(20, "български", "Bulgarian", "dmy", 1, 1026, 1026),
        new(21, "русский", "Russian", "dmy", 1, 1049, 1049),
        new(22, "Türkçe", "Turkish", "dmy", 1, 1055, 1055),
        new(23, "British", "British English", "dmy", 1, 2057, 1033),
        new(24, "eesti", "Estonian", "dmy", 1, 1061, 1061),
        new(25, "latviešu", "Latvian", "ymd", 1, 1062, 1062),
        new(26, "lietuvių", "Lithuanian", "ymd", 1, 1063, 1063),
        new(27, "Português (Brasil)", "Brazilian", "dmy", 7, 1046, 1046),
        new(28, "繁體中文", "Traditional Chinese", "ymd", 7, 1028, 1028),
        new(29, "한국어", "Korean", "ymd", 7, 1042, 1042),
        new(30, "简体中文", "Simplified Chinese", "ymd", 7, 2052, 2052),
        new(31, "Arabic", "Arabic", "dmy", 1, 1025, 1025),
        new(32, "ไทย", "Thai", "dmy", 7, 1054, 1054),
        new(33, "norsk (bokmål)", "Bokmål", "dmy", 1, 1044, 1044),
    ];

    /// <summary>The instance default — <c>us_english</c>, langid 0, DATEFIRST 7.</summary>
    public static readonly Language Default = All[0];

    /// <summary>
    /// Resolves an official name or an alias, the way <c>SET LANGUAGE</c> does.
    /// The match is case-insensitive; a name carrying diacritics
    /// (<c>Français</c>, <c>čeština</c>) has to be spelled with them, since
    /// real compares against the stored name rather than a folded form.
    /// </summary>
    public static Language? Find(string nameOrAlias)
    {
        foreach (var language in All)
        {
            if (string.Equals(language.Name, nameOrAlias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(language.Alias, nameOrAlias, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }
        return null;
    }
}
