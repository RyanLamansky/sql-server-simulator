using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// System-versioned tables written through <c>MERGE</c>, which EF Core uses for
/// every multi-row insert, and the period columns' upkeep around
/// <c>SET (SYSTEM_VERSIONING = OFF)</c> and an adopted history table (probed
/// 2026-10-02 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class TemporalMergeTests
{
    private const string CreateTemporal = """
        create table t (
            id int primary key, v int, n nvarchar(max),
            s datetime2 generated always as row start hidden not null,
            e datetime2 generated always as row end hidden not null,
            period for system_time (s, e)
        ) with (system_versioning = on (history_table = dbo.th))
        """;

    [TestMethod]
    public void Merge_StampsInsertedRowsAndVersionsUpdatedAndDeletedOnes()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporal,
            "insert t (id, v) values (1, 1), (2, 2)",
            """
            merge t using (values (1, 10), (3, 30)) as src (id, v) on t.id = src.id
            when matched then update set v = src.v
            when not matched then insert (id, v) values (src.id, src.v)
            when not matched by source then delete;
            """);

        AreEqual("1:10:max,3:30:max", simulation.ExecuteScalar(
            "select string_agg(concat(id, ':', v, ':', iif(e > '9999-01-01', 'max', 'closed')), ',') within group (order by id) from t"));
        AreEqual("1:1:closed,2:2:closed", simulation.ExecuteScalar(
            "select string_agg(concat(id, ':', v, ':', iif(e > '9999-01-01', 'max', 'closed')), ',') within group (order by id) from th"));
    }

    [TestMethod]
    public void Merge_InsertingSeveralRowsLikeEfCore_Succeeds()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporal,
            """
            merge t using (values (1, 1, 0), (2, 2, 1)) as i (id, v, _Position) on 1 = 0
            when not matched then insert (id, v) values (i.id, i.v)
            output inserted.e, inserted.s, i._Position;
            """);

        AreEqual(2, simulation.ExecuteScalar("select count(*) from t where e > '9999-01-01' and s < e"));
    }

    [TestMethod]
    public void UpdateAndMerge_AdvanceRowStart_WithVersioningOff()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporal,
            "insert t (id, v) values (1, 1)",
            "alter table t set (system_versioning = off)");

        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("""
            declare @s0 datetime2 = (select s from t where id = 1);
            waitfor delay '00:00:00.020';
            update t set v = 2 where id = 1;
            declare @s1 datetime2 = (select s from t where id = 1);
            waitfor delay '00:00:00.020';
            merge t using (values (1, 3)) as src (id, v) on t.id = src.id when matched then update set v = src.v;
            select concat(iif(@s1 > @s0, 'advanced', 'same'), ',', iif((select s from t where id = 1) > @s1, 'advanced', 'same'), ',', (select count(*) from th));
            """);
        AreEqual("advanced,advanced,0", command.ExecuteScalar());
    }

    /// <summary>
    /// A history row's off-row values live on the history table's heap, and a
    /// <c>FOR SYSTEM_TIME</c> read hands them to a reader of the parent's — a
    /// page index there named the parent's own unrelated chain, so an old
    /// version read back a newer row's text, or Msg 601 when the lengths
    /// differed.
    /// </summary>
    [TestMethod]
    public void ForSystemTime_ReadsHistoryRowsOffRowValues()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporal,
            "insert t (id, n) values (1, N'Sun')",
            "update t set n = N'Xyz'",
            "insert t (id, n) values (3, N'Moo')");

        AreEqual("1:Sun,1:Xyz,3:Moo", simulation.ExecuteScalar(
            "select string_agg(concat(id, ':', n), ',') within group (order by id, n) from t for system_time all"));
    }

    [TestMethod]
    public void ForSystemTime_ReadsAdoptedHistoryAfterPeriodRebuild()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporal,
            "insert t (id, n) values (1, N'Sun'), (2, N'Alpha')",
            "update t set n = N'Modified' + n",
            "alter table t set (system_versioning = off)",
            "alter table t drop period for system_time",
            "update th set s = '2000-01-01T01:00:00'",
            "update th set e = '2020-07-01T07:00:00'",
            "alter table t add period for system_time (s, e)",
            "alter table t set (system_versioning = on (history_table = dbo.th))");

        AreEqual("Sun,Alpha", simulation.ExecuteScalar(
            "select string_agg(n, ',') within group (order by id) from t for system_time as of '2010-01-01'"));
    }

    /// <summary>
    /// <c>ALTER COLUMN … { ADD | DROP } HIDDEN</c> toggles a period column's flag,
    /// repeating a toggle is a no-op, and a column that isn't a period column is
    /// Msg 13735; a history sibling's columns are never hidden.
    /// </summary>
    [TestMethod]
    public void AlterColumnHidden_TogglesPeriodColumnsOnly()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table h (id int primary key, v int, s datetime2 not null default '0001-01-01', e datetime2 not null default '9999-12-31T23:59:59.9999999')",
            "alter table h add period for system_time (s, e)",
            "alter table h alter column s add hidden",
            "alter table h alter column s add hidden",
            "alter table h alter column e add hidden",
            "alter table h alter column e drop hidden",
            "alter table h set (system_versioning = on (history_table = dbo.hh))");

        AreEqual("h.s,", simulation.ExecuteScalar(
            "select concat(string_agg(concat(object_name(object_id), '.', name), ','), ',') from sys.columns where is_hidden = 1 and object_id in (object_id('h'), object_id('hh'))"));
        simulation.AssertSqlError("alter table h alter column v add hidden", 13735,
            "Cannot alter HIDDEN attribute on column 'v' in table 'h' because this column is not a generated always column.");
    }

    [TestMethod]
    public void HistoryColumns_AreNeverHidden()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporal);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('th') and is_hidden = 1"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t') and is_hidden = 1"));
    }
}
