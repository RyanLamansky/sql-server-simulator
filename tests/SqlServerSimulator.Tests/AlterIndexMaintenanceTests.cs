using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER INDEX … REORGANIZE</c> and the resumable trio
/// (<c>RESUME</c> / <c>PAUSE</c> / <c>ABORT</c>). A flat page list has nothing
/// to compact and never pauses a build, so what's modeled is the validation and
/// the refusals — every message here was probed against SQL Server 2025, see
/// <c>docs/claude/indexes.md</c>.
/// </summary>
[TestClass]
public sealed class AlterIndexMaintenanceTests
{
    private const string Table = """
        create table t (a int not null primary key, b int, c varchar(max));
        create index ix_b on t(b);
        insert t values (1, 1, 'x'), (2, 2, 'y');
        """;

    // --- REORGANIZE succeeds and leaves the data alone ---

    [TestMethod]
    public void Reorganize_NamedIndex_Succeeds()
        => AreEqual(2, ExecuteScalar($"{Table} alter index ix_b on t reorganize; select count(*) from t"));

    [TestMethod]
    public void Reorganize_All_Succeeds()
        => AreEqual(2, ExecuteScalar($"{Table} alter index all on t reorganize; select count(*) from t"));

    [TestMethod]
    public void Reorganize_OnAConstraintBackedIndex_Succeeds()
        => AreEqual(2, ExecuteScalar($"""
            create table t (a int not null, b int, constraint pk_t primary key (a));
            insert t values (1, 1), (2, 2);
            alter index pk_t on t reorganize;
            select count(*) from t
            """));

    [TestMethod]
    public void Reorganize_OnATableWithNoIndexes_Succeeds()
        => AreEqual(1, ExecuteScalar("create table t (a int); insert t values (1); alter index all on t reorganize; select count(*) from t"));

    // --- REORGANIZE's own WITH (…) block ---

    [TestMethod]
    public void Reorganize_LobCompaction_Accepted()
        => AreEqual(2, ExecuteScalar($"{Table} alter index ix_b on t reorganize with (lob_compaction = on); select count(*) from t"));

    [TestMethod]
    public void Reorganize_CompressAllRowGroups_AcceptedOnARowstoreIndex()
        // Real takes the columnstore-shaped option on a rowstore index without
        // complaint, so the name isn't gated on the index kind.
        => AreEqual(2, ExecuteScalar($"{Table} alter index ix_b on t reorganize with (compress_all_row_groups = off); select count(*) from t"));

    [TestMethod]
    public void Reorganize_UnrecognizedOption_ReportsMsg155WithTheReorganizeWording()
        => AssertSqlError(
            $"{Table} alter index ix_b on t reorganize with (online = on)",
            155,
            "'online' is not a recognized ALTER INDEX REORGANIZE option.");

    [TestMethod]
    public void Reorganize_NonOnOffValue_ReportsMsg153()
        => AssertSqlError(
            $"{Table} alter index ix_b on t reorganize with (lob_compaction = 1)",
            153,
            "Invalid usage of the option lob_compaction in the INDEX statement.");

    [TestMethod]
    public void Reorganize_EmptyOptionList_IsASyntaxError()
        => AssertSqlError($"{Table} alter index ix_b on t reorganize with ()", 102, "Incorrect syntax near ')'.");

    // --- PARTITION = { ALL | n } ---

    [TestMethod]
    public void Reorganize_PartitionAll_Succeeds()
        => AreEqual(2, ExecuteScalar($"{Table} alter index ix_b on t reorganize partition = all; select count(*) from t"));

    [TestMethod]
    public void Reorganize_PartitionNumber_NamedIndex_ReportsMsg7729()
        => AssertSqlError(
            $"{Table} alter index ix_b on t reorganize partition = 1",
            7729,
            "Cannot specify partition number in the alter index statement as the index 'ix_b' is not partitioned.");

    [TestMethod]
    public void Reorganize_PartitionNumber_All_ReportsMsg7735NamingTheFirstIndex()
        => AssertSqlError(
            """
            create table t (a int not null, b int, constraint pk_t primary key (a));
            create index ix_b on t(b);
            alter index all on t reorganize partition = 2
            """,
            7735,
            "Cannot specify partition number in alter index statement to rebuild or reorganize a partition of index 'pk_t' as index is not partitioned.");

    [TestMethod]
    public void Reorganize_PartitionNumber_AllOverAnIndexlessTable_ReportsMsg7735NamingTheTable()
        => AssertSqlError(
            "create table t (a int); alter index all on t reorganize partition = 1",
            7735,
            "Cannot specify partition number in alter index statement to rebuild or reorganize a partition of table 't' as table is not partitioned.");

    [TestMethod]
    public void Rebuild_PartitionNumber_ReportsMsg7729()
        => AssertSqlError($"{Table} alter index ix_b on t rebuild partition = 1", 7729);

    // --- a disabled index ---

    [TestMethod]
    public void Reorganize_DisabledIndex_ReportsMsg1973()
        => AssertSqlError(
            $"{Table} alter index ix_b on t disable; alter index ix_b on t reorganize",
            1973,
            "Cannot perform the specified operation on disabled index 'ix_b' on table 't'.");

    [TestMethod]
    public void ReorganizeAll_StepsPastADisabledIndex()
        => AreEqual(2, ExecuteScalar($"{Table} alter index ix_b on t disable; alter index all on t reorganize; select count(*) from t"));

    // --- name resolution runs first ---

    [TestMethod]
    public void Reorganize_MissingIndex_ReportsMsg2727()
        => AssertSqlError($"{Table} alter index ix_nope on t reorganize", 2727, "Cannot find index 'ix_nope'.");

    [TestMethod]
    public void Reorganize_MissingTable_ReportsMsg1088()
        => AssertSqlError($"{Table} alter index ix_b on nope reorganize", 1088);

    // --- RESUME / PAUSE / ABORT: there is never anything to resume ---

    [TestMethod]
    public void Resume_ReportsMsg10638AtState1()
    {
        var exception = new Simulation().AssertSqlError($"{Table} alter index ix_b on t resume", 10638);
        AreEqual("ALTER INDEX 'RESUME' failed. There is no pending resumable index operation for the index 'ix_b' on 't'.", exception.Message);
        AreEqual((byte)1, exception.State);
    }

    [TestMethod]
    public void Pause_ReportsMsg10638AtState2()
    {
        var exception = new Simulation().AssertSqlError($"{Table} alter index ix_b on t pause", 10638);
        AreEqual("ALTER INDEX 'PAUSE' failed. There is no pending resumable index operation for the index 'ix_b' on 't'.", exception.Message);
        AreEqual((byte)2, exception.State);
    }

    [TestMethod]
    public void Abort_ReportsMsg10638AtState2()
        => AreEqual((byte)2, new Simulation().AssertSqlError($"{Table} alter index ix_b on t abort", 10638).State);

    [TestMethod]
    public void ResumeAll_ReportsMsg10680AtLevel11()
    {
        var exception = new Simulation().AssertSqlError($"{Table} alter index all on t resume", 10680);
        AreEqual("ALTER INDEX ALL 'RESUME' failed. There is no pending resumable index operation on 't'.", exception.Message);
        AreEqual((byte)11, exception.Class);
    }

    [TestMethod]
    public void PauseAll_OverAnIndexlessTable_StillReportsMsg10680()
        => AssertSqlError(
            "create table t (a int); alter index all on t pause",
            10680,
            "ALTER INDEX ALL 'PAUSE' failed. There is no pending resumable index operation on 't'.");

    [TestMethod]
    public void Resume_MissingIndex_ReportsMsg2727AheadOfTheRefusal()
        => AssertSqlError($"{Table} alter index ix_nope on t resume", 2727);

    [TestMethod]
    public void Pause_DisabledIndex_StillReportsMsg10638()
        // The resumable forms don't consult the disabled flag at all — real
        // reports the same refusal either way.
        => AssertSqlError($"{Table} alter index ix_b on t disable; alter index ix_b on t pause", 10638);

    [TestMethod]
    public void Resume_WithClause_IsValidatedAndDiscarded()
        => AssertSqlError($"{Table} alter index ix_b on t resume with (max_duration = 10 minutes, maxdop = 2)", 10638);

    [TestMethod]
    public void Resume_WaitAtLowPriority_IsAccepted()
        => AssertSqlError(
            $"{Table} alter index ix_b on t resume with (wait_at_low_priority (max_duration = 1 minutes, abort_after_wait = blockers))",
            10638);

    [TestMethod]
    public void Resume_UnrecognizedOption_ReportsThePlainMsg155()
        => AssertSqlError(
            $"{Table} alter index ix_b on t resume with (nope = 2)",
            155,
            "'nope' is not a recognized ALTER INDEX option.");

    [TestMethod]
    public void UnknownForm_IsASyntaxError()
        => AssertSqlError($"{Table} alter index ix_b on t frobnicate", 102);
}
