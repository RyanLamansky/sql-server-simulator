using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;

namespace SqlServerSimulator.Parser.FullText;

// The per-character overrides fitted to real's breaker.
internal static partial class FullTextWordBreaker
{
    private const byte ClassBreak = 1;
    private const byte ClassWord = 2;

    /// <summary>
    /// The per-character answers real's breaker gives where the rules here
    /// would differ, read off <c>sys.dm_fts_parser</c> for every BMP character
    /// between two x's (probed 2026-10-17 against SQL Server 2025).
    /// <c>Classes</c> holds, per BMP character, <see cref="ClassBreak"/> where
    /// real breaks a word at a character .NET counts a letter, mark or digit —
    /// the letters of scripts and blocks its tables predate, such as
    /// <c>U+023A</c>–<c>U+024F</c> and N'Ko — <see cref="ClassWord"/> where it
    /// keeps in a word one .NET doesn't (unassigned code points, Braille, the
    /// Kangxi radicals), and 0 where <see cref="IsWordChar"/>'s own rules
    /// agree. <c>CaseForms</c> is the form real gives a character whose case or
    /// compatibility fold differs from .NET's — a capital real leaves in upper
    /// case (<c>Ș</c>, <c>Ț</c>, <c>Ѐ</c>, the Georgian, Cherokee and Greek
    /// extended capitals), or a halfwidth Hangul letter real folds to the
    /// compatibility jamo rather than the conjoining one — taken in place of
    /// the lower-casing and the fold <see cref="Normalize"/> otherwise applies.
    /// </summary>
    private static readonly (byte[] Classes, FrozenDictionary<char, string> CaseForms) Overrides = LoadOverrides();

    /// <summary>The keys of <see cref="Overrides"/>' <c>CaseForms</c>, for the
    /// common case of a term holding none.</summary>
    private static readonly SearchValues<char> CaseFormKeys = SearchValues.Create([.. Overrides.CaseForms.Keys]);

    private static (byte[], FrozenDictionary<char, string>) LoadOverrides()
    {
        // B lines are the ranges real breaks at, W lines the ranges it keeps in
        // a word, and C lines a character's normal form.
        using var stream = typeof(FullTextWordBreaker).Assembly.GetManifestResourceStream("SqlServerSimulator.FullText.CharacterClasses.tsv")
            ?? throw new InvalidOperationException("The full-text character class resource is missing.");
        using var reader = new StreamReader(stream);
        var classes = new byte[char.MaxValue + 1];
        var forms = new Dictionary<char, string>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            switch (fields[0])
            {
                case "B":
                    classes.AsSpan(Hex(fields[1])..(Hex(fields[2]) + 1)).Fill(ClassBreak);
                    break;
                case "C":
                    var form = new char[fields[2].Length / 4];
                    for (var i = 0; i < form.Length; i++)
                        form[i] = (char)Hex(fields[2].Substring(i * 4, 4));
                    forms[(char)Hex(fields[1])] = new string(form);
                    break;
                case "W":
                    classes.AsSpan(Hex(fields[1])..(Hex(fields[2]) + 1)).Fill(ClassWord);
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized full-text character class line: {line}");
            }
        }
        return (classes, forms.ToFrozenDictionary());
    }

    private static int Hex(string text) => int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
