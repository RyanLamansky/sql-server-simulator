using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The culture data <c>FORMAT</c> reads: SQL Server formats through the .NET
/// Framework on Windows, whose NLS culture data differs from the ICU data .NET
/// reads here in most cultures — separators, currency and percent patterns,
/// month and day names, AM / PM designators, date patterns, calendars and era
/// names. The embedded <c>FormatCultures.tsv</c> carries real's values for every
/// culture name probed, read off <c>FORMAT</c>'s own output (probed 2026-09-29
/// against SQL Server 2025), and each is applied over a clone of a .NET culture
/// once and cached.
/// </summary>
/// <remarks>
/// <para>A name the table lacks resolves the way Windows synthesizes one: the
/// longest known prefix of its language, script and region supplies the data,
/// and the currency symbol comes from the region (<c>de-US</c> formats German
/// with <c>$</c>, <c>fr-QQ</c> French with <c>¤</c>); an unknown language takes
/// the generic data an unknown name gets.</para>
/// <para>The resource's lines are a region's currency symbol
/// (<c>@RR</c>, symbol), a culture's full field list, or a culture written as
/// another's fields with numbered replacements (<c>&lt;parent</c>,
/// <c>index=value</c>); the field order is <see cref="Field"/>'s.</para>
/// </remarks>
internal sealed class FormatCulture
{
    /// <summary>The field order of <c>FormatCultures.tsv</c>.</summary>
    private enum Field
    {
        NumberDecimalSeparator, NumberGroupSeparator, NumberGroupSizes, NumberDecimalDigits, NumberNegativePattern, NegativeSign, PositiveSign,
        CurrencySymbol, CurrencyDecimalSeparator, CurrencyGroupSeparator, CurrencyGroupSizes, CurrencyDecimalDigits, CurrencyPositivePattern, CurrencyNegativePattern,
        PercentSymbol, PercentDecimalSeparator, PercentGroupSeparator, PercentGroupSizes, PercentDecimalDigits, PercentPositivePattern, PercentNegativePattern, PerMilleSymbol,
        Calendar, ShortDatePattern, LongDatePattern, ShortTimePattern, LongTimePattern, FullDateTimePattern, MonthDayPattern, YearMonthPattern,
        AMDesignator, PMDesignator, DateSeparator, TimeSeparator, Era,
        DayNames, AbbreviatedDayNames, MonthNames, AbbreviatedMonthNames, MonthGenitiveNames, AbbreviatedMonthGenitiveNames,
        UniversalFullDateTimePattern, UniversalMonthNames, UniversalDayNames,
        Count,
    }

    /// <summary>The culture <c>FORMAT</c> uses when it's given none.</summary>
    internal static FormatCulture Default => Resolve("en-US");

    private static readonly ConcurrentDictionary<string, FormatCulture> cache = new(StringComparer.OrdinalIgnoreCase);

    private static (FrozenDictionary<string, string[]> Profiles, FrozenDictionary<string, string> RegionSymbols)? tables;

    /// <summary>The .NET culture carrying real's data.</summary>
    internal readonly CultureInfo Culture;

    /// <summary>What a custom date pattern's <c>g</c> / <c>gg</c> writes, which .NET gives no setter for.</summary>
    internal readonly string Era;

    /// <summary>
    /// The culture <c>'U'</c> formats through: the Framework switches a culture
    /// whose calendar isn't Gregorian to the Gregorian calendar's own pattern
    /// and month names for it (<c>ar-SA</c>'s is <c>dd MMMM, yyyy hh:mm:ss tt</c>
    /// with <c>نوفمبر</c> for November); <see cref="Culture"/> itself otherwise.
    /// </summary>
    internal readonly CultureInfo Universal;

    private FormatCulture(CultureInfo culture, string era, CultureInfo universal)
    {
        this.Culture = culture;
        this.Era = era;
        this.Universal = universal;
    }

    /// <summary>
    /// The culture for a name real's <c>FORMAT</c> accepts; the caller has
    /// already refused a name Windows can't parse.
    /// </summary>
    internal static FormatCulture Resolve(string name) => cache.GetOrAdd(name, static name => Build(name));

    /// <summary>Whether <paramref name="name"/> is a culture the table carries under exactly that name.</summary>
    internal static bool IsKnown(string name) => Profiles.ContainsKey(name.Replace('_', '-'));

    private static FrozenDictionary<string, string[]> Profiles => (tables ??= Load()).Profiles;

    private static FormatCulture Build(string name)
    {
        var normalized = name.Replace('_', '-');
        if (Profiles.TryGetValue(normalized, out var exact))
            return Create(exact, currencySymbol: null);

        // Windows synthesizes the rest: the longest known prefix of language,
        // script and region, with the region's own currency symbol.
        var parts = normalized.Split('-');
        if (parts[0].Length == 1)
            return Create(Profiles["qq"], currencySymbol: null);
        string? script = null, region = null;
        var i = 1;
        if (i < parts.Length && parts[i].Length == 4)
            script = parts[i++];
        if (i < parts.Length && parts[i].Length is 2 or 3)
            region = parts[i];
        string[]? profile;
        foreach (var candidate in (string?[])[
            script is not null && region is not null ? $"{parts[0]}-{script}-{region}" : null,
            region is not null ? $"{parts[0]}-{region}" : null,
            script is not null ? $"{parts[0]}-{script}" : null])
        {
            if (candidate is not null && Profiles.TryGetValue(candidate, out profile))
                return Create(profile, currencySymbol: null);
        }
        profile = Profiles.TryGetValue(parts[0], out var language) ? language : Profiles["qq"];
        var symbol = region is not null && (tables ??= Load()).RegionSymbols.TryGetValue(region, out var regional) ? regional : "¤";
        return Create(profile, symbol);
    }

    private static FormatCulture Create(string[] f, string? currencySymbol)
    {
        var baseName = f[(int)Field.Calendar] switch
        {
            "hebrew" => "he-IL",
            "hijri" => "ar-SA",
            "korean" => "ko-KR",
            "persian" => "fa-IR",
            "taiwan" => "zh-TW",
            "thai" => "th-TH",
            "umalqura" => "ar-SA",
            _ => "",
        };
        CultureInfo culture;
        try
        {
            culture = (CultureInfo)CultureInfo.GetCultureInfo(baseName).Clone();
        }
        catch (CultureNotFoundException)
        {
            culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        }

        var n = culture.NumberFormat;
        n.NumberDecimalSeparator = f[(int)Field.NumberDecimalSeparator];
        n.NumberGroupSeparator = f[(int)Field.NumberGroupSeparator];
        n.NumberGroupSizes = Sizes(f[(int)Field.NumberGroupSizes]);
        n.NumberDecimalDigits = Int(f[(int)Field.NumberDecimalDigits]);
        n.NumberNegativePattern = Int(f[(int)Field.NumberNegativePattern]);
        n.NegativeSign = f[(int)Field.NegativeSign];
        n.PositiveSign = f[(int)Field.PositiveSign];
        n.CurrencySymbol = currencySymbol ?? f[(int)Field.CurrencySymbol];
        n.CurrencyDecimalSeparator = f[(int)Field.CurrencyDecimalSeparator];
        n.CurrencyGroupSeparator = f[(int)Field.CurrencyGroupSeparator];
        n.CurrencyGroupSizes = Sizes(f[(int)Field.CurrencyGroupSizes]);
        n.CurrencyDecimalDigits = Int(f[(int)Field.CurrencyDecimalDigits]);
        n.CurrencyPositivePattern = Int(f[(int)Field.CurrencyPositivePattern]);
        n.CurrencyNegativePattern = Int(f[(int)Field.CurrencyNegativePattern]);
        n.PercentSymbol = f[(int)Field.PercentSymbol];
        n.PercentDecimalSeparator = f[(int)Field.PercentDecimalSeparator];
        n.PercentGroupSeparator = f[(int)Field.PercentGroupSeparator];
        n.PercentGroupSizes = Sizes(f[(int)Field.PercentGroupSizes]);
        n.PercentDecimalDigits = Int(f[(int)Field.PercentDecimalDigits]);
        n.PercentPositivePattern = Int(f[(int)Field.PercentPositivePattern]);
        n.PercentNegativePattern = Int(f[(int)Field.PercentNegativePattern]);
        n.PerMilleSymbol = f[(int)Field.PerMilleSymbol];

        var d = culture.DateTimeFormat;
        d.Calendar = f[(int)Field.Calendar] switch
        {
            "hebrew" => new HebrewCalendar(),
            "hijri" => new HijriCalendar(),
            "korean" => new KoreanCalendar(),
            "persian" => new PersianCalendar(),
            "taiwan" => new TaiwanCalendar(),
            "thai" => new ThaiBuddhistCalendar(),
            "umalqura" => new UmAlQuraCalendar(),
            _ => new GregorianCalendar(),
        };
        d.ShortDatePattern = f[(int)Field.ShortDatePattern];
        d.LongDatePattern = f[(int)Field.LongDatePattern];
        d.ShortTimePattern = f[(int)Field.ShortTimePattern];
        d.LongTimePattern = f[(int)Field.LongTimePattern];
        d.FullDateTimePattern = f[(int)Field.FullDateTimePattern];
        d.MonthDayPattern = f[(int)Field.MonthDayPattern];
        d.YearMonthPattern = f[(int)Field.YearMonthPattern];
        d.AMDesignator = f[(int)Field.AMDesignator];
        d.PMDesignator = f[(int)Field.PMDesignator];
        d.DateSeparator = f[(int)Field.DateSeparator];
        d.TimeSeparator = f[(int)Field.TimeSeparator];
        d.DayNames = f[(int)Field.DayNames].Split('|');
        d.AbbreviatedDayNames = f[(int)Field.AbbreviatedDayNames].Split('|');
        d.MonthNames = [.. f[(int)Field.MonthNames].Split('|'), ""];
        d.AbbreviatedMonthNames = [.. f[(int)Field.AbbreviatedMonthNames].Split('|'), ""];
        d.MonthGenitiveNames = [.. f[(int)Field.MonthGenitiveNames].Split('|'), ""];
        d.AbbreviatedMonthGenitiveNames = [.. f[(int)Field.AbbreviatedMonthGenitiveNames].Split('|'), ""];

        var universal = culture;
        if (f[(int)Field.UniversalFullDateTimePattern].Length > 0)
        {
            universal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            var u = universal.DateTimeFormat;
            u.FullDateTimePattern = f[(int)Field.UniversalFullDateTimePattern];
            u.AMDesignator = d.AMDesignator;
            u.PMDesignator = d.PMDesignator;
            // The Gregorian pattern's day names are the culture's own unless
            // real gives it others (`ps` writes `يونۍ` for Sunday where its
            // Persian calendar's own list starts `یکشنبه`).
            u.DayNames = u.AbbreviatedDayNames = f[(int)Field.UniversalDayNames].Length > 0
                ? f[(int)Field.UniversalDayNames].Split('|')
                : d.DayNames;
            if (f[(int)Field.UniversalDayNames].Length == 0)
                u.AbbreviatedDayNames = d.AbbreviatedDayNames;
            u.MonthNames = u.MonthGenitiveNames = [.. f[(int)Field.UniversalMonthNames].Split('|'), ""];
        }

        return new FormatCulture(culture, f[(int)Field.Era], universal);
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    private static int[] Sizes(string s) => Array.ConvertAll(s.Split(';'), Int);

    private static (FrozenDictionary<string, string[]> Profiles, FrozenDictionary<string, string> RegionSymbols) Load()
    {
        using var stream = typeof(FormatCulture).Assembly.GetManifestResourceStream("SqlServerSimulator.Format.Cultures.tsv")
            ?? throw new InvalidOperationException("The FORMAT culture resource is missing.");
        using var reader = new StreamReader(stream);
        var rows = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var regions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split('\t');
            for (var i = 1; i < cells.Length; i++)
                cells[i] = Unescape(cells[i]);
            if (cells[0].StartsWith('@'))
            {
                regions[cells[0][1..]] = cells[1];
                continue;
            }

            string[] fields;
            if (cells.Length > 1 && cells[1].StartsWith('<'))
            {
                fields = (string[])rows[cells[1][1..]].Clone();
                for (var i = 2; i < cells.Length; i++)
                {
                    var eq = cells[i].IndexOf('=', StringComparison.Ordinal);
                    fields[Int(cells[i][..eq])] = cells[i][(eq + 1)..];
                }
            }
            else
            {
                fields = cells[1..];
            }

            if (fields.Length != (int)Field.Count)
                throw new InvalidOperationException($"FORMAT culture row '{cells[0]}' carries {fields.Length} fields.");
            rows[cells[0]] = fields;
        }

        return (rows.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), regions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static string Unescape(string s) =>
        !s.Contains('\\', StringComparison.Ordinal) ? s : s.Replace("\\t", "\t", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}
