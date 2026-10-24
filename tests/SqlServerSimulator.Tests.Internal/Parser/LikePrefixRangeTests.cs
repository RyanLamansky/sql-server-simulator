using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Holds <see cref="LikePrefixRange"/> to the bounds it derives from a
/// pattern: the run of printable ASCII ahead of the first wildcard, cut after
/// its last letter or digit, the one character stepped — or the run of
/// letters and digits alone. The seek is result-transparent — any range
/// holding every match answers the same — so these pin how narrow the range
/// is, which is what a prefix's spaces and punctuation would otherwise cost.
/// </summary>
[TestClass]
public sealed class LikePrefixRangeTests
{
    [TestMethod]
    [DataRow("Sm%", null, "Sl", "Sn")]
    [DataRow("Sm", null, "Sl", "Sn")]
    [DataRow("Customer 123%", null, "Customer 122", "Customer 124")]
    [DataRow("Smith, J%", null, "Smith, I", "Smith, K")]
    [DataRow("O'Brien%", null, "O'Briem", "O'Brieo")]
    [DataRow("(555) 01%", null, "(555) 00", "(555) 02")]
    [DataRow("Smith, %", null, "Smitg", "Smiti")]
    [DataRow("A-9%", null, "A-8", "B")]
    [DataRow("9-Z%", null, "9-Y", null)]
    [DataRow("Sm\\%x%", "\\", "Sm%w", "Sm%y")]
    [DataRow("Sm_x%", null, "Sl", "Sn")]
    [DataRow("Abéc%", null, "Aa", "Ac")]
    public void TheBounds_StepTheLastLetterOrDigitOfThePrintableAsciiPrefix(string pattern, string? escape, string lower, string? upper)
    {
        IsTrue(LikePrefixRange.TryGet(pattern, escape?[0], throughPunctuation: true, out var actualLower, out var actualUpper));
        AreEqual(lower, actualLower);
        AreEqual(upper, actualUpper);
    }

    [TestMethod]
    [DataRow("%m%")]
    [DataRow("_m%")]
    [DataRow("[S]m%")]
    [DataRow("- %")]
    [DataRow("Émile%")]
    [DataRow("")]
    public void APrefixWithNoLetterOrDigit_GivesNoRange(string pattern) =>
        IsFalse(LikePrefixRange.TryGet(pattern, null, throughPunctuation: true, out _, out _));

    [TestMethod]
    [DataRow("Customer 123%", "Customeq", "Customes")]
    [DataRow("Smith, J%", "Smitg", "Smiti")]
    [DataRow("Sm%", "Sl", "Sn")]
    [DataRow("Az-9%", "Ay", "B")]
    public void TheLettersAndDigitsAlone_StopAtTheFirstOtherCharacter(string pattern, string lower, string? upper)
    {
        IsTrue(LikePrefixRange.TryGet(pattern, null, throughPunctuation: false, out var actualLower, out var actualUpper));
        AreEqual(lower, actualLower);
        AreEqual(upper, actualUpper);
    }

    [TestMethod]
    public void APrefixLedByPunctuation_HoldsNoLettersAndDigitsToSeekOnAlone() =>
        IsFalse(LikePrefixRange.TryGet("-m%", null, throughPunctuation: false, out _, out _));
}
