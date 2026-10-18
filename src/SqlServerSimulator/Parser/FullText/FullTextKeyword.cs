using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// The <c>keyword</c> a full-text term is stored under, and the
/// <c>display_term</c> real reconstructs from it, as
/// <c>sys.dm_fts_parser</c> and the index keyword DMVs report them.
/// </summary>
/// <remarks>
/// <para>
/// Real stores a term as its letters with their accents taken off, in UTF-16
/// big-endian, and when any letter carried an accent it appends
/// <c>0x0000</c> and one byte per letter up to the last accented one: 2 for a
/// letter without one, an accent code otherwise — 14 acute, 15 grave, 18
/// circumflex, 19 diaeresis, 25 tilde, 26 ring, 28 cedilla and so on, a
/// letter with two marks taking a code of its own (<c>café</c> is
/// <c>0x0063006100660065</c> + <c>0x00000202020E</c>). An expanding letter
/// gives each of its letters a byte (<c>ǣ</c> is <c>ae</c> with two macrons),
/// some letters fold to another (<c>þ</c> to <c>th</c>, <c>ς</c> to
/// <c>σ</c>), and the combining marks and the letters real's tables don't
/// know drop out of the keyword altogether. The display term puts each
/// accented letter back from its base and code, so two letters sharing both
/// display alike (<c>ṹ</c> reads back as <c>ǚ</c>), and some come back in
/// upper case (<c>ÿ</c> as <c>Ÿ</c>, Greek <c>ά</c> as <c>Ά</c>).
/// </para>
/// <para>
/// The table (<c>KeywordAccents.tsv</c>) was read off
/// <c>sys.dm_fts_parser</c> for every BMP character between two letters,
/// keeping each one whose keyword differs from the character the word
/// breaker here emits for it (probed 2026-10-17 against SQL Server 2025):
/// <c>F</c> lines give a character's base and codes, <c>D</c> lines the
/// ranges that drop out, <c>R</c> lines the character a base and code read
/// back as. An accent-insensitive catalog keeps the bases and drops the codes.
/// </para>
/// </remarks>
internal static class FullTextKeyword
{
    /// <summary>The keyword real stores the <c>END OF FILE</c> entry under.</summary>
    public static readonly byte[] EndOfFile = [0xFF];

    private static readonly (FrozenDictionary<char, (string Base, byte[] Codes)> Forward, FrozenDictionary<(char, byte), char> Reverse) Tables = Load();

    /// <summary>
    /// The keyword and display term for <paramref name="term"/>, a term the
    /// word breaker emitted under the catalog's accent sensitivity.
    /// </summary>
    public static (byte[] Keyword, string Display) Encode(string term, bool accentSensitive)
    {
        var forward = Tables.Forward;
        var letters = new StringBuilder(term.Length);
        List<byte>? codes = null;
        var lastAccent = -1;
        foreach (var ch in term)
        {
            if (!forward.TryGetValue(ch, out var entry))
            {
                _ = letters.Append(ch);
                codes?.Add(2);
                continue;
            }
            for (var i = 0; i < entry.Base.Length; i++)
            {
                var code = accentSensitive && i < entry.Codes.Length ? entry.Codes[i] : (byte)2;
                if (code != 2)
                {
                    if (codes is null)
                    {
                        codes = new List<byte>(term.Length + 2);
                        for (var j = 0; j < letters.Length; j++)
                            codes.Add(2);
                    }
                    lastAccent = letters.Length;
                }
                _ = letters.Append(entry.Base[i]);
                codes?.Add(code);
            }
        }
        var baseText = letters.ToString();
        var baseBytes = Encoding.BigEndianUnicode.GetBytes(baseText);
        if (codes is null)
            return (baseBytes, baseText);

        var keyword = new byte[baseBytes.Length + 2 + lastAccent + 1];
        baseBytes.CopyTo(keyword, 0);
        for (var i = 0; i <= lastAccent; i++)
            keyword[baseBytes.Length + 2 + i] = codes[i];
        var display = new StringBuilder(baseText.Length);
        for (var i = 0; i < baseText.Length; i++)
        {
            _ = display.Append(i <= lastAccent && codes[i] != 2 && Tables.Reverse.TryGetValue((baseText[i], codes[i]), out var accented)
                ? accented
                : baseText[i]);
        }
        return (keyword, display.ToString());
    }

    /// <summary>
    /// The letters <paramref name="term"/>'s keyword is built from, without
    /// their accent codes: what an accent-insensitive catalog keeps of a term.
    /// </summary>
    public static string Bases(string term)
    {
        var forward = Tables.Forward;
        StringBuilder? builder = null;
        for (var i = 0; i < term.Length; i++)
        {
            if (!forward.TryGetValue(term[i], out var entry))
            {
                _ = builder?.Append(term[i]);
                continue;
            }
            builder ??= new StringBuilder(term.Length).Append(term, 0, i);
            _ = builder.Append(entry.Base);
        }
        return builder?.ToString() ?? term;
    }

    private static (FrozenDictionary<char, (string, byte[])>, FrozenDictionary<(char, byte), char>) Load()
    {
        using var stream = typeof(FullTextKeyword).Assembly.GetManifestResourceStream("SqlServerSimulator.FullText.KeywordAccents.tsv")
            ?? throw new InvalidOperationException("The full-text keyword accent resource is missing.");
        using var reader = new StreamReader(stream);
        var forward = new Dictionary<char, (string, byte[])>();
        var reverse = new Dictionary<(char, byte), char>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            switch (fields[0])
            {
                case "D":
                    for (var cp = Hex(fields[1]); cp <= Hex(fields[2]); cp++)
                        forward[(char)cp] = (string.Empty, []);
                    break;
                case "F":
                    var letters = new char[fields[2].Length / 4];
                    for (var i = 0; i < letters.Length; i++)
                        letters[i] = (char)Hex(fields[2].Substring(i * 4, 4));
                    forward[(char)Hex(fields[1])] = (new string(letters), fields.Length > 3 ? Convert.FromHexString(fields[3]) : []);
                    break;
                case "R":
                    reverse[((char)Hex(fields[1]), (byte)Hex(fields[2]))] = (char)Hex(fields[3]);
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized full-text keyword accent line: {line}");
            }
        }
        return (forward.ToFrozenDictionary(), reverse.ToFrozenDictionary());
    }

    private static int Hex(string text) => int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
