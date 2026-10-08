using System.Collections.Frozen;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Classifies a word against the member names of <typeparamref name="TEnum"/>,
/// answering exactly as <see cref="Enum.TryParse{TEnum}(ReadOnlySpan{char}, bool, out TEnum)"/>
/// with <c>ignoreCase</c> does.
/// That method compares the span to every member name in turn, which made
/// it the largest single cost of tokenizing a text the token memo doesn't
/// hold: a CPU profile of one-off statements put 15–29% of their time in
/// it, since every unquoted word is checked against the ~190 reserved
/// keywords.
/// </summary>
/// <remarks>
/// The dictionary answers a span of ASCII characters other than white space
/// and comma that doesn't start with a digit or sign — every word the
/// tokenizer reads from ASCII text.
/// Anything else goes to <see cref="Enum.TryParse{TEnum}(ReadOnlySpan{char}, bool, out TEnum)"/>
/// itself, which reads a leading digit or sign as a number, trims white
/// space, combines comma-separated names, and case-folds non-ASCII
/// characters onto ASCII names (<c>ı</c> onto <c>I</c>).
/// </remarks>
internal static class EnumNameLookup<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<string, TEnum>.AlternateLookup<ReadOnlySpan<char>> Names =
        Enum.GetNames<TEnum>()
            .ToFrozenDictionary(name => name, Enum.Parse<TEnum>, StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool TryParse(ReadOnlySpan<char> name, out TEnum value)
    {
        if (name.IsEmpty || char.IsAsciiDigit(name[0]) || name[0] is '-' or '+')
            return Enum.TryParse(name, ignoreCase: true, out value);
        foreach (var c in name)
        {
            if (c is > '\x7F' or ',' || char.IsWhiteSpace(c))
                return Enum.TryParse(name, ignoreCase: true, out value);
        }
        return Names.TryGetValue(name, out value);
    }
}
