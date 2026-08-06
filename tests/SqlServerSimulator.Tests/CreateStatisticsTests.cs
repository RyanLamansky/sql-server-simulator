using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>CREATE STATISTICS</c> / <c>DROP STATISTICS</c> and
/// the <c>sys.stats</c> / <c>sys.stats_columns</c> rows they produce. The
/// simulator models the declaration rather than a histogram — nothing about
/// query execution reads a statistic — so the contract is catalog identity.
/// Probed against SQL Server 2025 on 2026-08-06.
/// </summary>
[TestClass]
public sealed class CreateStatisticsTests
{
    private const string Table = "create table t (a int not null primary key, b int null, c nvarchar(50) null);";

    [TestMethod]
    public void CreateStatistics_ReportsUserCreatedRow()
        => AreEqual("st_1/2/uc=1/ac=0/nr=0", new Simulation().ExecuteScalar($"""
            {Table}
            create statistics st_1 on t (b, c);
            select concat(s.name, '/', s.stats_id, '/uc=', s.user_created, '/ac=', s.auto_created, '/nr=', s.no_recompute)
            from sys.stats s where s.object_id = object_id('t') and s.user_created = 1
            """));

    /// <summary>
    /// stats_id continues past every index id on the table — the PK's backing
    /// index takes 1, so the first standalone statistic takes 2.
    /// </summary>
    [TestMethod]
    public void StatsId_ContinuesPastTheIndexIds()
        => AreEqual("3 4", new Simulation().ExecuteScalar($"""
            {Table}
            create index ix on t (b);
            create statistics st_1 on t (c);
            create statistics st_2 on t (b);
            select string_agg(cast(s.stats_id as varchar(10)), ' ') within group (order by s.stats_id)
            from sys.stats s where s.object_id = object_id('t') and s.user_created = 1
            """));

    [TestMethod]
    public void StatsColumns_FollowDeclaredOrder()
        => AreEqual("1:b 2:c", new Simulation().ExecuteScalar($"""
            {Table}
            create statistics st_1 on t (b, c);
            select string_agg(concat(sc.stats_column_id, ':', col.name), ' ') within group (order by sc.stats_column_id)
            from sys.stats s
            join sys.stats_columns sc on sc.object_id = s.object_id and sc.stats_id = s.stats_id
            join sys.columns col on col.object_id = s.object_id and col.column_id = sc.column_id
            where s.object_id = object_id('t') and s.name = 'st_1'
            """));

    [TestMethod]
    public void WithNorecompute_SetsNoRecompute()
        => IsTrue(new Simulation().ExecuteScalar<bool>($"""
            {Table}
            create statistics st_1 on t (c) with norecompute;
            select s.no_recompute from sys.stats s where s.object_id = object_id('t') and s.name = 'st_1'
            """));

    /// <summary>
    /// The sampling options describe how real would scan the data to build a
    /// histogram there isn't one of here, so they parse and discard.
    /// </summary>
    [TestMethod]
    [DataRow("with fullscan")]
    [DataRow("with sample 50 percent")]
    [DataRow("with sample 100 rows")]
    [DataRow("with fullscan, norecompute")]
    public void SamplingOptions_Accepted(string options)
        => AreEqual(1, new Simulation().ExecuteScalar($"""
            {Table}
            create statistics st_1 on t (c) {options};
            select count(*) from sys.stats where object_id = object_id('t') and user_created = 1
            """));

    [TestMethod]
    public void DuplicateName_RaisesMsg1927()
        => AreEqual(
            "There are already statistics on table 't' named 'st_1'.",
            new Simulation().AssertSqlError($"""
                {Table}
                create statistics st_1 on t (b);
                create statistics st_1 on t (c)
                """, 1927).Message);

    /// <summary>
    /// Statistics and indexes share one per-table name space, so an index's
    /// name collides here too.
    /// </summary>
    [TestMethod]
    public void NameHeldByAnIndex_RaisesMsg1927()
        => AreEqual(
            "There are already statistics on table 't' named 'ix'.",
            new Simulation().AssertSqlError($"""
                {Table}
                create index ix on t (b);
                create statistics ix on t (c)
                """, 1927).Message);

    [TestMethod]
    public void MissingTable_RaisesMsg1088()
        => AreEqual(
            "Cannot find the object \"dbo.nope\" because it does not exist or you do not have permissions.",
            new Simulation().AssertSqlError("create statistics st_1 on dbo.nope (b)", 1088).Message);

    [TestMethod]
    public void MissingColumn_RaisesMsg1911()
        => AreEqual(
            "Column name 'zzz' does not exist in the target table, index or view.",
            new Simulation().AssertSqlError($"{Table} create statistics st_1 on t (zzz)", 1911).Message);

    [TestMethod]
    public void DropStatistics_RemovesTheRow()
        => AreEqual(0, new Simulation().ExecuteScalar($"""
            {Table}
            create statistics st_1 on t (b);
            drop statistics t.st_1;
            select count(*) from sys.stats where object_id = object_id('t') and user_created = 1
            """));

    [TestMethod]
    public void DropStatistics_AcceptsACommaList()
        => AreEqual(0, new Simulation().ExecuteScalar($"""
            {Table}
            create statistics st_1 on t (b);
            create statistics st_2 on t (c);
            drop statistics t.st_1, dbo.t.st_2;
            select count(*) from sys.stats where object_id = object_id('t') and user_created = 1
            """));

    [TestMethod]
    public void DropMissingStatistics_RaisesMsg3701()
        => AreEqual(
            "Cannot drop the statistics 'dbo.t.nope', because it does not exist or you do not have permission.",
            new Simulation().AssertSqlError($"{Table} drop statistics dbo.t.nope", 3701).Message);

    /// <summary>
    /// An index-backed statistic keeps reporting alongside the standalone
    /// ones — the two share the id sequence but not the user_created flag.
    /// </summary>
    [TestMethod]
    public void IndexBackedStatistics_StillReport()
        => AreEqual(3, new Simulation().ExecuteScalar($"""
            {Table}
            create index ix on t (b);
            create statistics st_1 on t (c);
            select count(*) from sys.stats where object_id = object_id('t')
            """));
}
