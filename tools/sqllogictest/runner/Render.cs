using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SltRunner;

static class Render
{
    /// <summary>Faithful rendering used for the sim-vs-real diff.</summary>
    public static string Raw(object v) => v switch
    {
        null or DBNull => "NULL",
        bool b => b ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        IFormattable fo => fo.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "NULL",
    };

    /// <summary>sqllogictest canonical rendering, keyed by the record's type char.</summary>
    public static string Slt(object v, char typeChar)
    {
        if (v is null or DBNull) return "NULL";
        switch (typeChar)
        {
            case 'I':
                return AsLong(v).ToString(CultureInfo.InvariantCulture);
            case 'R':
                return AsDouble(v).ToString("F3", CultureInfo.InvariantCulture);
            default:
                var s = Raw(v);
                if (s.Length == 0) return "(empty)";
                var sb = new StringBuilder(s.Length);
                foreach (var c in s)
                    sb.Append(c is < ' ' or > '~' ? '@' : c);
                return sb.ToString();
        }
    }

    static long AsLong(object v)
    {
        try
        {
            if (v is string s) return LeadingLong(s);
            if (v is bool b) return b ? 1 : 0;
            if (v is byte[]) return 0;
            var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
            return (long)d;
        }
        catch { return 0; }
    }

    static double AsDouble(object v)
    {
        try
        {
            if (v is string s) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
            if (v is bool b) return b ? 1 : 0;
            if (v is byte[]) return 0;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        catch { return 0; }
    }

    static long LeadingLong(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        var start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return long.TryParse(s.AsSpan(start, i - start), out var v) ? v : 0;
    }

    public static string Md5(IEnumerable<string> values)
    {
        var sb = new StringBuilder();
        foreach (var v in values) { sb.Append(v); sb.Append('\n'); }
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
