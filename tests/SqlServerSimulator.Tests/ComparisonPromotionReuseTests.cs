using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A comparison whose operands promote to a common type converts a written
/// string operand once per distinct string rather than once per row — the shape
/// <c>WHERE OrderDate &gt;= '2015-01-01'</c> makes on every scan. These pin that
/// the conversion still answers per row wherever the string actually varies:
/// alternating literals inside one statement, a string column against a date
/// column, a cached plan re-run with a different parameter, and a value that
/// fails to convert. Every expected value was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ComparisonPromotionReuseTests
{
    /// <summary>
    /// Four dated rows spanning either side of 2015 plus an all-NULL row, with
    /// each date also written as text (<c>s</c>) so a per-row string operand is
    /// available, and a numeric and a GUID column so the memo is exercised for
    /// the other string-parsing targets.
    /// </summary>
    private const string Seed = """
        create table d (id int not null primary key, dv date null, dt datetime null,
                        s varchar(30) null, n numeric(9,2) null, g uniqueidentifier null);
        insert d values
            (1, '2015-01-01', '2015-01-01T00:00:00', '2015-01-01', 1.50, '11111111-1111-1111-1111-111111111111'),
            (2, '2015-06-15', '2015-06-15T12:30:00', '2015-06-15', 2.25, '22222222-2222-2222-2222-222222222222'),
            (3, '2016-01-01', '2016-01-01T00:00:00', '2016-01-01', 3.00, '33333333-3333-3333-3333-333333333333'),
            (4, '2014-12-31', '2014-12-31T23:59:00', '2014-12-31', 4.75, '44444444-4444-4444-4444-444444444444'),
            (5, null, null, null, null, null);
        """;

    /// <summary>The matching ids, comma-joined, so a whole result set is one string.</summary>
    private static string Ids(string where)
    {
        var ids = new List<string>();
        using var reader = new Simulation().ExecuteReader($"{Seed} select id from d where {where} order by id");
        while (reader.Read())
            ids.Add(reader.GetInt32(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(',', ids);
    }

    [TestMethod]
    public void DateColumn_AgainstWrittenBounds_FiltersTheRange()
        => AreEqual("1,2", Ids("dv >= '2015-01-01' and dv < '2016-01-01'"));

    [TestMethod]
    public void DateColumn_Between_WrittenBounds_FiltersTheRange()
        => AreEqual("1,2", Ids("dv between '2015-01-01' and '2015-12-31'"));

    [TestMethod]
    public void DateTimeColumn_AgainstAWrittenTimestamp_ComparesTheTimeToo()
        => AreEqual("2,3", Ids("dt >= '2015-06-15 12:00'"));

    [TestMethod]
    public void NumericColumn_AgainstAWrittenNumber_ComparesNumerically()
        => AreEqual("2,3,4", Ids("n > '2.00'"));

    [TestMethod]
    public void GuidColumn_AgainstAWrittenGuid_ComparesAsAGuid()
        => AreEqual("2", Ids("g = '22222222-2222-2222-2222-222222222222'"));

    /// <summary>
    /// A string <em>column</em> on the value side: the operand differs on every
    /// row, so every row converts its own text.
    /// </summary>
    [TestMethod]
    public void DateColumn_AgainstAStringColumn_ConvertsEachRowsOwnText()
        => AreEqual("1,2,3,4", Ids("dv = s"));

    /// <summary>
    /// The string operand alternates mid-scan: rows 1-2 compare against one date
    /// and rows 3-5 against another, through a single comparison node.
    /// </summary>
    [TestMethod]
    public void AlternatingWrittenDate_ComparesEachRowAgainstItsOwn()
        => AreEqual("1,3", Ids("dv = case when id <= 2 then '2015-01-01' else '2016-01-01' end"));

    [TestMethod]
    public void AlternatingWrittenNumber_ComparesEachRowAgainstItsOwn()
        => AreEqual("2,4", Ids("n > case when id <= 2 then '2.00' else '4.00' end"));

    /// <summary>
    /// <c>BETWEEN</c> holds a memo per bound as well as one for the subject, so
    /// this alternates both bounds at once.
    /// </summary>
    [TestMethod]
    public void AlternatingBetweenBounds_CompareEachRowAgainstItsOwnPair()
        => AreEqual("1,2,4", Ids("""
            dv between case when id <= 2 then '2015-01-01' else '2014-01-01' end
                   and case when id <= 2 then '2015-12-31' else '2014-12-31' end
            """));

    /// <summary>
    /// A string comparison stays a string comparison — the memo declines a
    /// target inside the string category, so the varchar / nvarchar pair is
    /// converted the way it always was.
    /// </summary>
    [TestMethod]
    public void VarcharColumn_AgainstAnNVarcharLiteral_ComparesAsText()
        => AreEqual("2", Ids("s = N'2015-06-15'"));

    [TestMethod]
    public void StringColumn_AgainstAWrittenString_ComparesAsText()
        => AreEqual("2,3", Ids("s > '2015-01-01'"));

    [TestMethod]
    public void WrittenDates_InAnInList_ConvertEachElement()
        => AreEqual("1,3", Ids("dv in ('2015-01-01', '2016-01-01')"));

    /// <summary>
    /// A row whose text isn't a date raises real's own Msg 241 — the memo never
    /// holds a failed conversion, so the error surfaces from the row that
    /// carries it however many rows converted cleanly first.
    /// </summary>
    [TestMethod]
    public void UnconvertibleTextOnALaterRow_StillRaises()
        => _ = new Simulation().AssertSqlError(
            $"{Seed} select count(*) from d where dv >= case when id = 1 then '2015-01-01' else 'not-a-date' end",
            241);

    /// <summary>
    /// The plan cache shares one comparison node across executions, so a re-run
    /// with a different parameter has to convert the new value — and going back
    /// to the first one has to convert it back.
    /// </summary>
    [TestMethod]
    public void CachedPlan_ReRunWithADifferentBound_FiltersOnTheNewOne()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Seed);
        using var connection = simulation.CreateOpenConnection();
        AreEqual(3, AtOrAfter(connection, "2015-01-01"));
        AreEqual(1, AtOrAfter(connection, "2016-01-01"));
        AreEqual(4, AtOrAfter(connection, "2014-01-01"));
        AreEqual(3, AtOrAfter(connection, "2015-01-01"));

        static int AtOrAfter(DbConnection connection, string bound)
        {
            using var command = connection.CreateCommand("select count(*) from d where dv >= @p", ("@p", bound));
            return (int)command.ExecuteScalar()!;
        }
    }
}
