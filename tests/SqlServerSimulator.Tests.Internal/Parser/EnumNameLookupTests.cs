using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Holds <see cref="EnumNameLookup{TEnum}"/> to the answer
/// <see cref="Enum.TryParse{TEnum}(ReadOnlySpan{char}, bool, out TEnum)"/>
/// gives with <c>ignoreCase</c>, which is what the tokenizer asked before the
/// lookup replaced it: every member name in three spellings, and the inputs
/// the method reads other than by name.
/// </summary>
[TestClass]
public sealed class EnumNameLookupTests
{
    private static readonly string[] Unusual =
    [
        "", "_", "1", "+1", "-0", "007", " select", "select ", "select,from", "Add,All",
        "\u0131nsert", "\u017Felect", "SELECT\u0000", "selectx", "notakeyword", "NotChecked", "NotAKeyword",
        "@@rowcount", "$action", "#temp", "a$b", "x_y", "regexp_like", "\uFF33elect",
    ];

    private static void AgreesWithEnumTryParse<TEnum>()
        where TEnum : struct, Enum
    {
        foreach (var name in Enum.GetNames<TEnum>())
        {
            foreach (var spelling in (string[])[name, name.ToUpperInvariant(), name.ToLowerInvariant(), name + "x", name[..^1]])
                Agree<TEnum>(spelling);
        }
        foreach (var input in Unusual)
            Agree<TEnum>(input);
    }

    private static void Agree<TEnum>(string input)
        where TEnum : struct, Enum
    {
        var expected = Enum.TryParse<TEnum>(input.AsSpan(), ignoreCase: true, out var expectedValue);
        var actual = EnumNameLookup<TEnum>.TryParse(input, out var actualValue);
        AreEqual(expected, actual, $"'{input}'");
        AreEqual(expectedValue, actualValue, $"'{input}'");
    }

    [TestMethod]
    public void Keyword_AgreesWithEnumTryParse() => AgreesWithEnumTryParse<Keyword>();

    [TestMethod]
    public void ContextualKeyword_AgreesWithEnumTryParse() => AgreesWithEnumTryParse<ContextualKeyword>();

    [TestMethod]
    public void AtAtKeyword_AgreesWithEnumTryParse() => AgreesWithEnumTryParse<AtAtKeyword>();
}
