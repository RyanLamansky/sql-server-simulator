using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The ANSI / arithmetic session options apply when their <c>SET</c> runs, not
/// while the batch compiles, and <c>@@OPTIONS</c> reports them. Every
/// expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SessionOptionTimingTests
{
    private static List<object> Scalars(string batch)
    {
        using var reader = new Simulation().ExecuteReader(batch);
        var values = new List<object>();
        do
        {
            while (reader.Read())
                values.Add(reader.GetValue(0));
        }
        while (reader.NextResult());
        return values;
    }

    [TestMethod]
    [DataRow("select case when null = null then 1 else 0 end; set ansi_nulls off; select case when null = null then 1 else 0 end", "0,1")]
    [DataRow("select iif('a' + null is null, 'null', 'a'); set concat_null_yields_null off; select iif('a' + null is null, 'null', 'a')", "null,a")]
    [DataRow("select cast(sessionproperty('ARITHABORT') as int); set arithabort on; select cast(sessionproperty('ARITHABORT') as int)", "0,1")]
    [DataRow("if 1 = 0 set ansi_nulls off; select cast(sessionproperty('ANSI_NULLS') as int)", "1")]
    public void AnOption_AppliesWhenItsSetRuns(string batch, string expected)
        => AreEqual(expected, string.Join(",", Scalars(batch)));

    [TestMethod]
    public void AtAtOptions_TracksTheSession()
        => AreEqual("5432,5944,5936,5920,5984,1888,18272,26464,26432", string.Join(",", Scalars("""
            select @@options;
            set nocount on; select @@options;
            set ansi_warnings off; select @@options;
            set ansi_padding off; select @@options;
            set arithabort on; select @@options;
            set concat_null_yields_null off; select @@options;
            set xact_abort on; select @@options;
            set numeric_roundabort on; select @@options;
            set ansi_nulls off; select @@options
            """)));
}
