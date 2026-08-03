using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the equivalence <see cref="BuiltInToken"/> relies on to answer
/// most comparisons ordinally: over ASCII alphanumerics an
/// ordinal-ignore-case compare and the linguistic compare under
/// <c>IgnoreCase | IgnoreKanaType | IgnoreWidth</c> agree, so the cheaper
/// one may stand in. Every input outside that range has to keep reaching
/// the linguistic path, which is what the width- and control-character
/// cases below pin — those are the two shapes where the two comparisons
/// genuinely disagree.
/// </summary>
/// <remarks>
/// The matching regime itself (which sites route through this matcher, and
/// what real does at each) is covered end-to-end by
/// <c>NameComparisonRegimeTests</c> in the public-surface suite; this class
/// only guards the shortcut.
/// </remarks>
[TestClass]
public sealed class BuiltInTokenComparisonTests
{
    private const CompareOptions Options =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    /// <summary>The comparison <see cref="BuiltInToken.Equals(string?, string?)"/> is defined as.</summary>
    private static bool Linguistic(string x, string y) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(x, y, Options) == 0;

    private static readonly string[] Alphabet =
        [.. "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".Select(c => c.ToString())];

    [TestMethod]
    public void AsciiAlphanumerics_AgreeWithLinguisticCompare()
    {
        // Every one- and two-character word over the range the shortcut
        // admits, against every other: the shortcut is only sound if the two
        // comparisons never part company anywhere inside it.
        var words = new List<string>(Alphabet);
        foreach (var first in Alphabet)
        {
            foreach (var second in Alphabet)
                words.Add(first + second);
        }

        var mismatches = new List<string>();
        foreach (var x in words)
        {
            foreach (var y in words)
            {
                if (BuiltInToken.Equals(x, y) != Linguistic(x, y))
                    mismatches.Add($"'{x}' vs '{y}'");
            }
        }
        IsEmpty(mismatches);
    }

    [TestMethod]
    public void MixedCaseTokens_AgreeWithLinguisticCompare()
    {
        // The token values the matcher actually sees, in the spellings a
        // written statement can carry them in.
        string[] tokens = ["U", "FN", "IF", "TR", "SN", "PK", "UQ", "inserted", "deleted", "SCHEMA", "COLUMN", "sp_addextendedproperty"];
        foreach (var token in tokens)
        {
            foreach (var spelling in (string[])[token, token.ToUpperInvariant(), token.ToLowerInvariant()])
            {
                IsTrue(BuiltInToken.Equals(token, spelling), $"'{token}' vs '{spelling}'");
                AreEqual(Linguistic(token, spelling), BuiltInToken.Equals(token, spelling));
            }
        }
    }

    [TestMethod]
    public void FullwidthSpelling_StillMatchesItsAsciiToken()
    {
        // IgnoreWidth folds a fullwidth Ｓ onto an ASCII S, which an ordinal
        // compare would separate — probe-confirmed as real's behavior for
        // these sites, so the shortcut must decline here.
        IsTrue(BuiltInToken.Equals("ｓchema", "SCHEMA"));
        IsTrue(BuiltInToken.Equals("Ｕ", "U"));
        IsTrue(BuiltInToken.Equals("ｉnserted", "INSERTED"));
        IsTrue(BuiltInToken.EqualsAny("Ｕ", "FN", "U", "V"));
    }

    [TestMethod]
    public void ZeroWeightCharacters_StillCompareLinguistically()
    {
        // Control characters carry no collation weight, so the linguistic
        // compare reads these as equal where an ordinal one would not. The
        // shortcut has to decline, or the two would part company.
        const string WithSoh = "a\u0001";
        const string WithStx = "a\u0002";
        IsTrue(Linguistic(WithSoh, WithStx));
        IsTrue(BuiltInToken.Equals(WithSoh, WithStx));
        IsFalse(string.Equals(WithSoh, WithStx, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Underscores_AndOtherNonAlphanumerics_AgreeWithLinguisticCompare()
    {
        // Names outside the shortcut's range still have to answer correctly —
        // system-proc and level-type tokens are full of underscores.
        string[] names = ["sp_addextendedproperty", "SP_ADDEXTENDEDPROPERTY", "level0type", "LEVEL0TYPE", "fn_listextendedproperty", "sp_help"];
        foreach (var x in names)
        {
            foreach (var y in names)
                AreEqual(Linguistic(x, y), BuiltInToken.Equals(x, y), $"'{x}' vs '{y}'");
        }
    }

    [TestMethod]
    public void Nulls_CompareAsBefore()
    {
        IsTrue(BuiltInToken.Equals(null, null));
        IsFalse(BuiltInToken.Equals(null, "U"));
        IsFalse(BuiltInToken.Equals("U", null));
        IsFalse(BuiltInToken.EqualsAny(null, "U"));
    }

    [TestMethod]
    public void EqualsAny_MatchesTheSameSetAsRepeatedEquals()
    {
        string[] options = ["U", "FN", "IF", "V", "P", "TR", "SN", "PK", "UQ", "C", "D", "F"];
        foreach (var candidate in (string[])["U", "u", "Ｕ", "F", "XX", "", "TF", "pk"])
        {
            var anyMatched = options.Any(option => BuiltInToken.Equals(candidate, option));
            AreEqual(anyMatched, BuiltInToken.EqualsAny(candidate, options), $"'{candidate}'");
        }
    }

    [TestMethod]
    public void HashCode_StaysConsistentAcrossTheShortcutBoundary()
    {
        // Equality reaches across the boundary (an in-range token equals an
        // out-of-range fullwidth spelling of itself), so a dictionary keyed
        // by BuiltInToken.Comparer only works if the hash folds width too.
        foreach (var (inRange, outOfRange) in ((string, string)[])[("SCHEMA", "ｓchema"), ("U", "Ｕ"), ("INSERTED", "ｉnserted")])
        {
            IsTrue(BuiltInToken.Equals(inRange, outOfRange));
            AreEqual(BuiltInToken.GetHashCode(inRange), BuiltInToken.GetHashCode(outOfRange));
        }
    }
}
