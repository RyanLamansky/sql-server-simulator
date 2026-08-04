using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>LIKE</c> / <c>NOT LIKE</c> / <c>PATINDEX</c> translate their pattern once
/// per distinct pattern rather than once per row. The memo keys on the pattern
/// text, the escape character and the resolved collation's case sensitivity, so
/// these pin that every one of those three inputs still decides the answer: the
/// same predicate answers differently under a case-insensitive, case-sensitive
/// and accent-insensitive collation, a per-row pattern column matches each row's
/// own pattern, a per-row <c>ESCAPE</c> changes what counts as a wildcard, and a
/// cached plan re-run with a different parameter matches the new pattern.
/// Every expected value was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class LikePatternReuseTests
{
    /// <summary>
    /// Seven values in three collations, plus each row's own pattern and escape
    /// character. Rows 1-3 differ only in case, 4-5 add an umlaut (so an
    /// accent-insensitive collation folds them onto 1-3), and 6-7 carry the
    /// wildcard characters themselves so an <c>ESCAPE</c> clause has something
    /// to protect.
    /// </summary>
    private const string Seed = """
        create table c (
            id int not null primary key,
            ci nvarchar(50) collate Latin1_General_CI_AS not null,
            cs nvarchar(50) collate Latin1_General_CS_AS not null,
            ai nvarchar(50) collate Latin1_General_CI_AI not null,
            pat nvarchar(50) collate Latin1_General_CI_AS not null,
            esc nchar(1) collate Latin1_General_CI_AS not null);
        insert c values
            (1, N'USB cable', N'USB cable', N'USB cable', N'USB%',       N'\'),
            (2, N'usb cable', N'usb cable', N'usb cable', N'usb%',       N'!'),
            (3, N'Usb Cable', N'Usb Cable', N'Usb Cable', N'%cable',     N'\'),
            (4, N'ÜSB cable', N'ÜSB cable', N'ÜSB cable', N'U_B%',       N'!'),
            (5, N'üsb_cable', N'üsb_cable', N'üsb_cable', N'%[a-c]able', N'\'),
            (6, N'A%B',       N'A%B',       N'A%B',       N'A\%B',       N'\'),
            (7, N'A_B',       N'A_B',       N'A_B',       N'A!_B',       N'!');
        """;

    /// <summary>The matching ids, comma-joined, so a whole result set is one string.</summary>
    private static string Ids(string where)
    {
        var ids = new List<string>();
        using var reader = new Simulation().ExecuteReader($"{Seed} select id from c where {where} order by id");
        while (reader.Read())
            ids.Add(reader.GetInt32(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(',', ids);
    }

    [TestMethod]
    public void CaseInsensitive_UpperPattern_MatchesEveryCasing()
        => AreEqual("1,2,3", Ids("ci like N'USB%'"));

    [TestMethod]
    public void CaseSensitive_UpperPattern_MatchesOnlyTheUpperRow()
        => AreEqual("1", Ids("cs like N'USB%'"));

    [TestMethod]
    public void CaseSensitive_LowerPattern_MatchesOnlyTheLowerRow()
        => AreEqual("2", Ids("cs like N'usb%'"));

    /// <summary>
    /// The same three inputs as the case-sensitive test above, differing only in
    /// the underscore wildcard — a case-sensitive collation still decides each
    /// character, so only the exactly-cased row matches.
    /// </summary>
    [TestMethod]
    public void CaseSensitive_UnderscoreWildcard_StaysCaseSensitive()
        => AreEqual("2", Ids("cs like N'u_b%'"));

    /// <summary>
    /// An accent-insensitive collation still folds case, which is the half of
    /// the collation the pattern translation reads. (Its accent half is a
    /// separate, pre-existing gap: real answers <c>1,2,3,4,5</c> here because
    /// <c>ai</c> folds the umlaut too — see the LIKE section of
    /// <c>docs/claude/collations.md</c>.)
    /// </summary>
    [TestMethod]
    public void AccentInsensitive_UpperPattern_StillFoldsCase()
        => AreEqual("1,2,3", Ids("ai like N'USB%'"));

    /// <summary>
    /// The accent-<em>sensitive</em> counterpart: under CI_AS the umlaut is a
    /// different character, so the same pattern picks out only rows 4-5.
    /// </summary>
    [TestMethod]
    public void AccentSensitive_UmlautPattern_MatchesOnlyTheUmlautRows()
        => AreEqual("4,5", Ids("ci like N'ÜSB%'"));

    [TestMethod]
    public void CaseSensitive_NotLike_ComplementsTheMatch()
        => AreEqual("2,3,4,5,6,7", Ids("cs not like N'USB%'"));

    /// <summary>An explicit <c>COLLATE</c> outranks the column's own collation.</summary>
    [TestMethod]
    public void ExplicitCollateOnTheSubject_DecidesCaseSensitivity()
        => AreEqual("1,2,3", Ids("cs collate Latin1_General_CI_AS like N'USB%'"));

    [TestMethod]
    public void LeadingWildcard_MatchesAnySuffix()
        => AreEqual("1,2,3,4,5", Ids("ci like N'%cable'"));

    [TestMethod]
    public void UnescapedPercentInPattern_IsAWildcard()
        => AreEqual("6,7", Ids("ci like N'A%B'"));

    [TestMethod]
    public void EscapedPercentInPattern_IsALiteral()
        => AreEqual("6", Ids(@"ci like N'A\%B' escape N'\'"));

    [TestMethod]
    public void UnderscoreInsideAClass_IsALiteral()
        => AreEqual("7", Ids("ci like N'A[_]B'"));

    [TestMethod]
    public void CharacterClass_MatchesEitherMember()
        => AreEqual("1,2,3", Ids("ci like N'[Uu]%'"));

    [TestMethod]
    public void NegatedCharacterClass_MatchesTheRest()
        => AreEqual("4,5,6,7", Ids("ci like N'[^Uu]%'"));

    /// <summary>
    /// The pattern is a column, so it differs on every row — the memo misses
    /// each time and each row is matched against its own pattern.
    /// </summary>
    [TestMethod]
    public void PerRowPattern_MatchesEachRowsOwnPattern()
        => AreEqual("1,2,3,5", Ids("ci like pat"));

    /// <summary>
    /// Both the pattern and the escape character vary per row, which is what
    /// makes rows 6 and 7 join the match: each row's own escape neutralizes the
    /// wildcard its own pattern carries.
    /// </summary>
    [TestMethod]
    public void PerRowPatternAndEscape_MatchesEachRowsOwnPair()
        => AreEqual("1,2,3,5,6,7", Ids("ci like pat escape esc"));

    /// <summary>
    /// A pattern that is a wildcard under one escape character and a literal
    /// under another, evaluated by the same node in one statement.
    /// </summary>
    [TestMethod]
    public void SameNodeDifferentEscape_AnswersPerRow()
        => AreEqual("6,7", Ids(@"ci like case when id = 6 then N'A\%B' else N'A%B' end escape case when id = 6 then N'\' else N'!' end"));

    /// <summary>
    /// One fixed pattern, two escape characters: under <c>_</c> the pattern's
    /// own underscore is escaped and <c>A_B</c> reads as the literal <c>AB</c>,
    /// under <c>!</c> it stays a wildcard. Only the escape character differs, so
    /// this is what makes the escape a load-bearing part of the memo's key.
    /// </summary>
    [TestMethod]
    public void SameNodeSamePatternDifferentEscape_AnswersPerRow()
        => AreEqual("7", Ids("ci like N'A_B' escape case when id = 6 then N'_' else N'!' end"));

    [TestMethod]
    public void PatIndex_PerRowPattern_ReportsEachRowsOwnPosition()
    {
        var positions = new List<string>();
        using var reader = new Simulation().ExecuteReader($"{Seed} select patindex(pat, ci) from c order by id");
        while (reader.Read())
            positions.Add(reader.GetInt32(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AreEqual("1,1,5,0,5,0,0", string.Join(',', positions));
    }

    [TestMethod]
    public void PatIndex_FixedPatterns_ReportTheirOwnPositions()
    {
        var rows = new List<string>();
        using var reader = new Simulation().ExecuteReader(
            $"{Seed} select patindex(N'%[a-c]able', ci), patindex(N'usb%', ci), patindex(N'A[%]B', ci) from c order by id");
        while (reader.Read())
            rows.Add($"{reader.GetInt32(0)}/{reader.GetInt32(1)}/{reader.GetInt32(2)}");
        AreEqual("5/1/0 5/1/0 5/1/0 5/0/0 5/0/0 0/0/1 0/0/0", string.Join(' ', rows));
    }

    /// <summary>
    /// The plan cache shares one <c>LIKE</c> node across executions, so a
    /// re-run with a different parameter has to translate the new pattern —
    /// and going back to the first one has to translate it back.
    /// </summary>
    [TestMethod]
    public void CachedPlan_ReRunWithADifferentPattern_MatchesTheNewOne()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Seed);
        using var connection = simulation.CreateOpenConnection();
        AreEqual(3, Matches(connection, "USB%"));
        AreEqual(2, Matches(connection, "A%B"));
        AreEqual(5, Matches(connection, "%cable"));
        AreEqual(3, Matches(connection, "USB%"));

        static int Matches(DbConnection connection, string pattern)
        {
            using var command = connection.CreateCommand("select count(*) from c where ci like @p", ("@p", pattern));
            return (int)command.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// A NULL pattern is UNKNOWN whatever pattern the same node matched on the
    /// row before, and the rows around it still match their own patterns.
    /// </summary>
    [TestMethod]
    public void NullPatternOnSomeRows_IsUnknownThereAndNowhereElse()
        => AreEqual("1,3,5", Ids("ci like case when id = 2 then cast(null as nvarchar(50)) else pat end"));
}
