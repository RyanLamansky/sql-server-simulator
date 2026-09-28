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

    /// <summary>
    /// A procedure, trigger or dynamic-SQL body's SET of these options applies
    /// inside the body and reverts when it returns — save ANSI_NULLS, which a
    /// procedure body ignores for the setting it was created under (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ModuleBodySet_AppliesInsideAndReverts()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("""
            create procedure p as begin
                set ansi_warnings off; set ansi_padding off; set arithabort on;
                set concat_null_yields_null off; set numeric_roundabort on; set ansi_nulls off;
                select concat(@@options, ' ', null + 'a');
            end
            """);
        using var reader = sim.CreateOpenConnection().CreateCommand("exec p; select @@options").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("9568 a", reader.GetValue(0));
        IsTrue(reader.NextResult() && reader.Read());
        AreEqual(5432, reader.GetValue(0));
    }

    [TestMethod]
    public void DynamicSqlSet_OfAnsiNulls_AppliesToItsBatch()
        => AreEqual("0 1 32", string.Join(",", Scalars("""
            declare @r varchar(10)
            exec sp_executesql N'set ansi_nulls off; set @r = concat(@@options & 32, '' '', case when null = null then 1 else 0 end)', N'@r varchar(10) output', @r output
            select concat(@r, ' ', @@options & 32)
            """)));

    [TestMethod]
    public void TriggerBodySet_Reverts()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int); create table l (o int)",
            "create trigger tr on t after insert as begin set concat_null_yields_null off; insert l values (@@options & 4096); end");
        AreEqual(4096, sim.ExecuteScalar("insert t values (1); select @@options & 4096"));
        AreEqual(0, sim.ExecuteScalar("select o from l"));
    }

    /// <summary>
    /// QUOTED_IDENTIFIER is settled as the batch parses, so every read of
    /// <c>@@OPTIONS</c> in the batch sees what its last SET of the option left
    /// — even the read written ahead of the SET (probed 2026-09-28 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select @@options & 256; set quoted_identifier off; select @@options & 256", "0,0")]
    [DataRow("set quoted_identifier off; select @@options & 256; set quoted_identifier on; select @@options & 256", "256,256")]
    [DataRow("select @@options & 256; if 1 = 0 set ansi_defaults off; select @@options & 256", "0,0")]
    public void AtAtOptions_QuotedIdentifier_ReadsTheBatchsLastSet(string batch, string expected)
        => AreEqual(expected, string.Join(",", Scalars(batch)));
}
