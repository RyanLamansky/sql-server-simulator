using System.Collections.Frozen;

namespace SqlServerSimulator;

internal abstract partial class Collation
{
    /// <summary>
    /// The family index per Windows collation-name prefix — the low seven bits
    /// of <c>COLLATIONPROPERTY(name, 'CollationId')</c>. Probed 2026-09-26 from
    /// SQL Server 2025 over the complete <c>fn_helpcollations()</c> catalog,
    /// where every collation sharing a prefix shares it save the version-90
    /// names in <see cref="Version90FamilyIndexByPrefix"/>.
    /// </summary>
    private static readonly FrozenDictionary<string, int> FamilyIndexByPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Albanian"] = 24,
        ["Arabic"] = 1,
        ["Assamese"] = 90,
        ["Azeri_Cyrillic"] = 100,
        ["Azeri_Latin"] = 99,
        ["Bashkir"] = 84,
        ["Bengali"] = 89,
        ["Bosnian_Cyrillic"] = 79,
        ["Bosnian_Latin"] = 78,
        ["Breton"] = 38,
        ["Chinese_PRC"] = 36,
        ["Chinese_PRC_Stroke"] = 46,
        ["Chinese_Simplified_Pinyin"] = 67,
        ["Chinese_Simplified_Stroke_Order"] = 68,
        ["Chinese_Taiwan_Bopomofo"] = 47,
        ["Chinese_Taiwan_Stroke"] = 3,
        ["Chinese_Traditional_Bopomofo"] = 66,
        ["Chinese_Traditional_Pinyin"] = 69,
        ["Chinese_Traditional_Stroke_Count"] = 65,
        ["Chinese_Traditional_Stroke_Order"] = 70,
        ["Corsican"] = 15,
        ["Croatian"] = 22,
        ["Cyrillic_General"] = 21,
        ["Czech"] = 4,
        ["Danish_Greenlandic"] = 71,
        ["Danish_Norwegian"] = 5,
        ["Dari"] = 2,
        ["Divehi"] = 60,
        ["Estonian"] = 29,
        ["Finnish_Swedish"] = 10,
        ["French"] = 11,
        ["Frisian"] = 96,
        ["Georgian_Modern_Sort"] = 45,
        ["German_PhoneBook"] = 41,
        ["Greek"] = 7,
        ["Hebrew"] = 12,
        ["Hungarian"] = 13,
        ["Hungarian_Technical"] = 42,
        ["Icelandic"] = 14,
        ["Indic_General"] = 53,
        ["Japanese"] = 16,
        ["Japanese_Bushu_Kakusu"] = 73,
        ["Japanese_Unicode"] = 43,
        ["Japanese_XJIS"] = 72,
        ["Kazakh"] = 55,
        ["Khmer"] = 94,
        ["Korean"] = 64,
        ["Korean_Wansung"] = 17,
        ["Lao"] = 95,
        ["Latin1_General"] = 8,
        ["Latvian"] = 30,
        ["Lithuanian"] = 31,
        ["Macedonian_FYROM"] = 58,
        ["Maltese"] = 85,
        ["Maori"] = 18,
        ["Mapudungan"] = 82,
        ["Modern_Spanish"] = 40,
        ["Mohawk"] = 63,
        ["Nepali"] = 98,
        ["Norwegian"] = 74,
        ["Pashto"] = 91,
        ["Persian"] = 81,
        ["Polish"] = 19,
        ["Romanian"] = 20,
        ["Romansh"] = 75,
        ["Sami_Norway"] = 86,
        ["Sami_Sweden_Finland"] = 87,
        ["Serbian_Cyrillic"] = 77,
        ["Serbian_Latin"] = 76,
        ["Slovak"] = 23,
        ["Slovenian"] = 28,
        ["Syriac"] = 59,
        ["Tamazight"] = 97,
        ["Tatar"] = 57,
        ["Thai"] = 25,
        ["Tibetan"] = 92,
        ["Traditional_Spanish"] = 9,
        ["Turkish"] = 26,
        ["Turkmen"] = 88,
        ["Uighur"] = 37,
        ["Ukrainian"] = 27,
        ["Upper_Sorbian"] = 83,
        ["Urdu"] = 80,
        ["Uzbek_Latin"] = 56,
        ["Vietnamese"] = 32,
        ["Welsh"] = 93,
        ["Yakut"] = 6,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The prefixes whose <c>_90</c> collations carry a family index of their
    /// own (probed with <see cref="FamilyIndexByPrefix"/>).
    /// </summary>
    private static readonly FrozenDictionary<string, int> Version90FamilyIndexByPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Chinese_Hong_Kong_Stroke"] = 62,
        ["Chinese_PRC"] = 49,
        ["Chinese_PRC_Stroke"] = 50,
        ["Chinese_Taiwan_Bopomofo"] = 51,
        ["Chinese_Taiwan_Stroke"] = 52,
        ["Japanese"] = 48,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>COLLATIONPROPERTY(name, 'CollationId')</c>, which packs the family
    /// index (bits 0–6), <c>_UTF8</c> (bit 7), <c>_SC</c> (bit 8),
    /// <c>_BIN2</c> (bit 11), the ignore-case / -accent / -kana / -width
    /// flags (bits 12–15), <c>_BIN</c> (bit 16), the version ordinal (bits
    /// 17 up) and a SQL_* collation's sort order (bits 24–31); a version-140
    /// collation sets <c>_SC</c> implicitly, and bit 9 unless it is
    /// <c>_VSS</c>. A SQL_*
    /// collation takes the lowest family index among the Windows prefixes of
    /// its LCID (probed 2026-09-26 against SQL Server 2025, all 5540 names).
    /// </summary>
    private static int CollationIdFor(string name, string prefix, CollationFlags flags, int versionOrdinal, int lcid)
    {
        int id;
        if (name.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase))
        {
            var family = int.MaxValue;
            foreach (var (windowsPrefix, index) in FamilyIndexByPrefix)
            {
                if (index < family && LcidAndCodePageByPrefix.TryGetValue(windowsPrefix, out var entry) && entry.Lcid == lcid)
                    family = index;
            }
            id = family == int.MaxValue ? 0 : family;
            if (SqlServerSortOrders.TryGetValue(name, out var sortOrder))
                id |= sortOrder.OrderNumber << 24;
        }
        else
        {
            id = versionOrdinal == 1 && Version90FamilyIndexByPrefix.TryGetValue(prefix, out var version90) ? version90
                : FamilyIndexByPrefix.GetValueOrDefault(prefix);
        }

        var binary = flags.HasFlag(CollationFlags.Binary) || flags.HasFlag(CollationFlags.Binary2);
        if (!binary && versionOrdinal == 3)
            id |= flags.HasFlag(CollationFlags.VariationSelectorSensitive) ? 0x100 : 0x300;
        if (!binary)
        {
            id |= (flags.HasFlag(CollationFlags.CaseInsensitive) ? 0x1000 : 0)
                | (flags.HasFlag(CollationFlags.AccentInsensitive) ? 0x2000 : 0)
                | (flags.HasFlag(CollationFlags.KanaSensitive) ? 0 : 0x4000)
                | (flags.HasFlag(CollationFlags.WidthSensitive) ? 0 : 0x8000);
        }
        return id
            | (flags.HasFlag(CollationFlags.Utf8) ? 0x80 : 0)
            | (flags.HasFlag(CollationFlags.SupplementaryCharacters) ? 0x100 : 0)
            | (flags.HasFlag(CollationFlags.Binary2) ? 0x800 : 0)
            | (flags.HasFlag(CollationFlags.Binary) ? 0x10000 : 0)
            | (versionOrdinal << 17);
    }
}
