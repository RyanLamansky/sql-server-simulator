using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the per-object creation-time <c>QUOTED_IDENTIFIER</c>
/// capture: a module body parses under the setting in effect when it was
/// created, not the session that invokes it, and the capture surfaces through
/// <c>sys.sql_modules.uses_quoted_identifier</c> /
/// <c>OBJECTPROPERTY(id, 'IsQuotedIdentOn' | 'ExecIsQuotedIdentOn')</c>.
/// The companion half is Msg 1934 — real refuses to write to a table whose
/// stored expressions it would have to re-evaluate under the wrong setting —
/// plus Msg 1935 for indexing a view that captured the wrong one.
/// Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class QuotedIdentifierCaptureTests
{
    /// <summary>
    /// Runs <paramref name="batches"/> with the session flipped to
    /// <c>QUOTED_IDENTIFIER OFF</c> first, so every object they create
    /// captures OFF. The returned simulation's later connections open at the
    /// default ON, which is the asymmetry these tests are about.
    /// </summary>
    private static Simulation CreatedUnderOff(params IEnumerable<string> batches)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches([.. new[] { "set quoted_identifier off" }.Concat(batches)]);
        return simulation;
    }

    // ----- Module bodies run under the captured setting -----

    [TestMethod]
    public void Procedure_CreatedUnderOff_BodyReadsDoubleQuotesAsLiteral()
        => AreEqual("off-lit", CreatedUnderOff("create procedure p as select \"off-lit\" as v").ExecuteScalar("exec p"));

    [TestMethod]
    public void View_CreatedUnderOff_BodyReadsDoubleQuotesAsLiteral()
        => AreEqual("v-lit", CreatedUnderOff("create view v as select \"v-lit\" as v").ExecuteScalar("select v from v"));

    [TestMethod]
    public void ScalarFunction_CreatedUnderOff_BodyReadsDoubleQuotesAsLiteral()
        => AreEqual("f-lit", CreatedUnderOff("create function f() returns varchar(20) as begin return \"f-lit\" end").ExecuteScalar("select dbo.f()"));

    [TestMethod]
    public void InlineTvf_CreatedUnderOff_BodyReadsDoubleQuotesAsLiteral()
        => AreEqual("i-lit", CreatedUnderOff("create function f() returns table as return select \"i-lit\" as v").ExecuteScalar("select v from dbo.f()"));

    [TestMethod]
    public void MultiStatementTvf_CreatedUnderOff_BodyReadsDoubleQuotesAsLiteral()
        => AreEqual("m-lit", CreatedUnderOff("""
            create function f() returns @r table (v varchar(20)) as
            begin insert @r values ("m-lit"); return end
            """).ExecuteScalar("select v from dbo.f()"));

    [TestMethod]
    public void Trigger_CreatedUnderOff_BodyReadsDoubleQuotesAsLiteral()
        => AreEqual("tr-lit", CreatedUnderOff(
            "create table t (a int)",
            "create trigger tr on t after insert as select \"tr-lit\" as v").ExecuteScalar("insert t values (1)"));

    /// <summary>
    /// <c>@@OPTIONS</c> bit 256 reports the captured setting inside the body,
    /// so the module observes its own capture rather than the caller's.
    /// </summary>
    [TestMethod]
    public void CapturedOff_BodyReadsZeroFromOptionsBit()
        => AreEqual(0, CreatedUnderOff("create procedure p as select @@options & 256 as qibit").ExecuteScalar("exec p"));

    /// <summary>
    /// Each module in a call chain runs under its own capture — the ON-created
    /// caller keeps reading identifiers while the OFF-created callee reads
    /// literals.
    /// </summary>
    [TestMethod]
    public void NestedModules_EachRunUnderItsOwnCapture()
    {
        var simulation = CreatedUnderOff("create procedure inner_p as select \"inner-lit\" as v");
        simulation.ExecuteBatches(
            "set quoted_identifier on",
            "create procedure outer_p as begin select @@options & 256 as qibit; exec inner_p; end");
        using var reader = simulation.ExecuteReader("exec outer_p");
        IsTrue(reader.Read());
        AreEqual(256, reader.GetValue(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("inner-lit", reader.GetValue(0));
    }

    /// <summary>
    /// Dynamic SQL inherits the enclosing module's captured setting rather than
    /// the session's, because the capture is applied to the session for the
    /// body's duration.
    /// </summary>
    [TestMethod]
    public void DynamicSqlInsideCapturedOffModule_InheritsTheCapture()
        => AreEqual("dyn", CreatedUnderOff("create procedure p as exec('select \"dyn\" as v')").ExecuteScalar("exec p"));

    /// <summary>
    /// The swap is scoped to the body: the invoking session's own setting is
    /// unchanged once the module returns.
    /// </summary>
    [TestMethod]
    public void CapturedOffModule_DoesNotLeakIntoTheCallingSession()
    {
        var simulation = CreatedUnderOff("create procedure p as select \"off-lit\" as v");
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("exec p").ExecuteScalar();
        AreEqual(256, connection.CreateCommand("select @@options & 256").ExecuteScalar());
    }

    // ----- Catalog surfaces -----

    [TestMethod]
    public void SqlModules_UsesQuotedIdentifier_ReflectsTheCapture()
    {
        var simulation = CreatedUnderOff("create procedure p_off as select 1 as v");
        simulation.ExecuteBatches("set quoted_identifier on", "create procedure p_on as select 1 as v");
        IsFalse(simulation.ExecuteScalar<bool>("select uses_quoted_identifier from sys.sql_modules where object_id = object_id('p_off')"));
        IsTrue(simulation.ExecuteScalar<bool>("select uses_quoted_identifier from sys.sql_modules where object_id = object_id('p_on')"));
    }

    [TestMethod]
    public void ObjectProperty_IsQuotedIdentOn_ReflectsTheCapture()
        => AreEqual(0, CreatedUnderOff("create procedure p as select 1 as v")
            .ExecuteScalar("select objectproperty(object_id('p'), 'IsQuotedIdentOn')"));

    [TestMethod]
    public void ObjectProperty_ExecIsQuotedIdentOn_ReflectsTheCapture()
        => AreEqual(0, CreatedUnderOff("create procedure p as select 1 as v")
            .ExecuteScalar("select objectproperty(object_id('p'), 'ExecIsQuotedIdentOn')"));

    /// <summary>
    /// A table answers 1 for the shorter spelling no matter which setting
    /// created it, and NULL for the module-only <c>ExecIs…</c> spelling —
    /// the one place the two spellings disagree (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void ObjectProperty_OnTableCreatedUnderOff_IsOneAndExecFormIsNull()
    {
        var simulation = CreatedUnderOff("create table t (a int)");
        AreEqual(1, simulation.ExecuteScalar("select objectproperty(object_id('t'), 'IsQuotedIdentOn')"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select objectproperty(object_id('t'), 'ExecIsQuotedIdentOn')"));
    }

    // ----- ALTER re-stamps -----

    [TestMethod]
    public void AlterUnderOn_RestampsTheCaptureAndTheBody()
    {
        var simulation = CreatedUnderOff("create procedure p as select \"off-lit\" as v");
        simulation.ExecuteBatches("set quoted_identifier on", "alter procedure p as select 'on-lit' as v");
        AreEqual(1, simulation.ExecuteScalar("select objectproperty(object_id('p'), 'IsQuotedIdentOn')"));
        AreEqual("on-lit", simulation.ExecuteScalar("exec p"));
    }

    [TestMethod]
    public void CreateOrAlterUnderOff_RestampsToOff()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create or alter procedure p as select 'on-lit' as v",
            "set quoted_identifier off",
            "create or alter procedure p as select \"off-lit\" as v");
        AreEqual(0, simulation.ExecuteScalar("select objectproperty(object_id('p'), 'IsQuotedIdentOn')"));
        AreEqual("off-lit", simulation.ExecuteScalar("exec p"));
    }

    [TestMethod]
    public void AlterViewUnderOn_RestampsTheCapture()
    {
        var simulation = CreatedUnderOff("create view v as select \"off-lit\" as v");
        simulation.ExecuteBatches("set quoted_identifier on", "alter view v as select 'on-lit' as v");
        AreEqual("on-lit", simulation.ExecuteScalar("select v from v"));
    }

    // ----- Msg 1934: writes to a table with a captured expression -----

    /// <summary>
    /// Msg 1934's full text: <paramref name="verb"/> echoing the statement and
    /// <paramref name="options"/> the comma-separated offender list.
    /// </summary>
    private static string Msg1934(string verb, string options = "QUOTED_IDENTIFIER") =>
        $"{verb} failed because the following SET options have incorrect settings: '{options}'."
        + " Verify that SET options are correct for use with indexed views and/or indexes on computed columns"
        + " and/or filtered indexes and/or query notifications and/or XML data type methods and/or spatial index operations.";

    [TestMethod]
    public void Insert_IntoPersistedComputedColumnTable_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b as a * 2 persisted)");
        simulation.AssertSqlError("set quoted_identifier off; insert t (a) values (1)", 1934, Msg1934("INSERT"));
    }

    [TestMethod]
    public void Update_OfFilteredIndexTable_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int primary key, b int);
            create unique index ix on t (b) where b > 5
            """);
        simulation.AssertSqlError("set quoted_identifier off; update t set b = 3 where a = 1", 1934, Msg1934("UPDATE"));
    }

    [TestMethod]
    public void Delete_FromComputedColumnIndexedTable_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int primary key, b as a * 2);
            create index ix on t (b)
            """);
        simulation.AssertSqlError("set quoted_identifier off; delete t where a = 1", 1934, Msg1934("DELETE"));
    }

    [TestMethod]
    public void Merge_IntoXmlIndexedTable_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int primary key, x xml);
            create primary xml index ix on t (x)
            """);
        simulation.AssertSqlError("""
            set quoted_identifier off;
            merge t as d using (select 1 as a) s on d.a = s.a when not matched then insert (a) values (s.a)
            """, 1934, Msg1934("MERGE"));
    }

    [TestMethod]
    public void Insert_IntoSpatialIndexedTable_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int primary key, g geometry);
            create spatial index ix on t (g) with (bounding_box = (0, 0, 10, 10))
            """);
        _ = simulation.AssertSqlError("set quoted_identifier off; insert t values (1, geometry::Point(1, 1, 0))", 1934);
    }

    [TestMethod]
    public void Insert_IntoIndexedViewBaseTable_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table b (a int not null primary key, c int not null)",
            "create view v with schemabinding as select a, c, count_big(*) as n from dbo.b group by a, c",
            "create unique clustered index ix on v (a, c)");
        _ = simulation.AssertSqlError("set quoted_identifier off; insert b values (1, 1)", 1934);
    }

    /// <summary>
    /// The gate is about maintaining a stored expression, so a non-persisted
    /// computed column with no index over it doesn't trigger it — nor does a
    /// read of any of the gated shapes (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void Insert_IntoNonPersistedComputedColumnTable_UnderOff_Succeeds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b as a * 2)");
        AreEqual(2, simulation.ExecuteScalar("set quoted_identifier off; insert t (a) values (1); select b from t"));
    }

    [TestMethod]
    public void Select_FromPersistedComputedColumnTable_UnderOff_Succeeds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int primary key, b as a * 2 persisted);
            insert t (a) values (1)
            """);
        AreEqual(2, simulation.ExecuteScalar("set quoted_identifier off; select b from t"));
    }

    /// <summary>
    /// A disabled filtered index isn't maintained, so it stops gating writes
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void Insert_WithDisabledFilteredIndex_UnderOff_Succeeds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int primary key, b int);
            create unique index ix on t (b) where b > 5;
            alter index ix on t disable
            """);
        AreEqual(1, simulation.ExecuteScalar("set quoted_identifier off; insert t values (1, 9); select count(*) from t"));
    }

    /// <summary>
    /// The gate reads the effective setting, so a module created under OFF
    /// trips it even when the calling session has QUOTED_IDENTIFIER ON — and
    /// only when the body runs: real accepts the CREATE itself
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void CapturedOffProcedure_WritingToGatedTable_Raises1934AtExecutionNotCreate()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b as a * 2 persisted)");
        simulation.ExecuteBatches("set quoted_identifier off", "create procedure p as insert t (a) values (1)");
        _ = simulation.AssertSqlError("exec p", 1934);
    }

    /// <summary>
    /// Real gates the batch, not the executed path, so a never-taken branch
    /// still raises (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void GatedWriteInsideNeverTakenBranch_UnderOff_StillRaises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b as a * 2 persisted)");
        _ = simulation.AssertSqlError("set quoted_identifier off; if 1 = 0 insert t (a) values (1); select 1", 1934);
    }

    // ----- Msg 1934: DDL that captures an expression -----

    [TestMethod]
    public void CreateTable_WithPersistedComputedColumn_UnderOff_Raises1934()
        => new Simulation().AssertSqlError(
            "set quoted_identifier off; create table t (a int, b as a * 2 persisted)",
            1934,
            Msg1934("CREATE TABLE"));

    [TestMethod]
    public void CreateTable_WithNonPersistedComputedColumn_UnderOff_Succeeds()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            set quoted_identifier off;
            create table t (a int, b as a * 2);
            insert t (a) values (1);
            select b from t
            """));

    [TestMethod]
    public void AlterTable_AddPersistedComputedColumn_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b int)");
        simulation.AssertSqlError("set quoted_identifier off; alter table t add c as (b * 2) persisted;", 1934, Msg1934("ALTER TABLE"));
    }

    [TestMethod]
    public void CreateIndex_Filtered_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b int)");
        simulation.AssertSqlError("set quoted_identifier off; create unique index ix on t (b) where b > 5", 1934, Msg1934("CREATE INDEX"));
    }

    [TestMethod]
    public void CreateIndex_OverComputedColumn_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b as a * 2)");
        _ = simulation.AssertSqlError("set quoted_identifier off; create index ix on t (b)", 1934);
    }

    [TestMethod]
    public void CreateIndex_OverPlainColumns_UnderOff_Succeeds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b int)");
        AreEqual(0, simulation.ExecuteScalar("set quoted_identifier off; create index ix on t (b); select count(*) from t"));
    }

    /// <summary>
    /// <c>CREATE SPATIAL INDEX</c> keeps the bare <c>CREATE INDEX</c> verb but
    /// narrows the verify clause to spatial operations alone — the one wording
    /// variant in the Msg 1934 family (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void CreateSpatialIndex_UnderOff_Raises1934WithSpatialOnlyWording()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, g geometry)");
        simulation.AssertSqlError(
            "set quoted_identifier off; create spatial index ix on t (g) with (bounding_box = (0, 0, 10, 10))",
            1934,
            "CREATE INDEX failed because the following SET options have incorrect settings: 'QUOTED_IDENTIFIER'."
            + " Verify that SET options are correct for use with spatial index operations.");
    }

    [TestMethod]
    public void CreatePrimaryXmlIndex_UnderOff_Raises1934NamingThatStatement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, x xml)");
        simulation.AssertSqlError("set quoted_identifier off; create primary xml index ix on t (x)", 1934, Msg1934("CREATE PRIMARY XML INDEX"));
    }

    // ----- Msg 1934: XML data type methods -----

    [TestMethod]
    public void XmlValueMethod_UnderOff_Raises1934()
        => new Simulation().AssertSqlError(
            "set quoted_identifier off; declare @x xml = '<a>1</a>'; select @x.value('(/a)[1]', 'int')",
            1934,
            Msg1934("SELECT"));

    [TestMethod]
    public void XmlExistMethod_UnderOff_Raises1934()
        => _ = new Simulation().AssertSqlError(
            "set quoted_identifier off; declare @x xml = '<a>1</a>'; select @x.exist('/a')", 1934);

    /// <summary>
    /// The error names the enclosing statement, not always SELECT.
    /// </summary>
    [TestMethod]
    public void XmlValueMethod_InsideInsert_UnderOff_NamesInsert()
        => new Simulation().AssertSqlError("""
            set quoted_identifier off;
            declare @x xml = '<a>1</a>';
            declare @t table (v int);
            insert @t select @x.value('(/a)[1]', 'int')
            """, 1934, Msg1934("INSERT"));

    /// <summary>
    /// <c>.nodes()</c> on its own is exempt; a <c>.value()</c> against the node
    /// it produced is not (probe-confirmed both ways).
    /// </summary>
    [TestMethod]
    public void XmlNodesMethodAlone_UnderOff_Succeeds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, x xml); insert t values (1, '<a><b>7</b></a>')");
        AreEqual(1, simulation.ExecuteScalar("set quoted_identifier off; select count(*) from t cross apply x.nodes('/a/b') n (nd)"));
    }

    [TestMethod]
    public void XmlNodesWithValue_UnderOff_Raises1934()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, x xml); insert t values (1, '<a><b>7</b></a>')");
        _ = simulation.AssertSqlError("set quoted_identifier off; select n.nd.value('.', 'int') from t cross apply x.nodes('/a/b') n (nd)", 1934);
    }

    // ----- Msg 1935: the view's own capture -----

    /// <summary>
    /// Indexing a view created under OFF fails even from a session with
    /// QUOTED_IDENTIFIER ON — the view's capture is the problem, and only
    /// recreating it helps (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void CreateIndexOnViewCapturedUnderOff_Raises1935EvenFromAnOnSession()
    {
        var simulation = CreatedUnderOff(
            "create table b (a int not null primary key, c int not null)",
            "create view v with schemabinding as select a, c, count_big(*) as n from dbo.b group by a, c");
        simulation.AssertSqlError(
            "create unique clustered index ix on v (a, c)",
            1935,
            "Cannot create index. Object 'v' was created with the following SET options off: 'QUOTED_IDENTIFIER'.");
    }

    /// <summary>
    /// An ON-created view falls through to the session's own Msg 1934 instead.
    /// </summary>
    [TestMethod]
    public void CreateIndexOnOnCapturedView_FromAnOffSession_Raises1934()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table b (a int not null primary key, c int not null)",
            "create view v with schemabinding as select a, c, count_big(*) as n from dbo.b group by a, c");
        _ = simulation.AssertSqlError("set quoted_identifier off; create unique clustered index ix on v (a, c)", 1934);
    }

    // ----- Msg 1934: NOEXPAND over an indexed view -----

    /// <summary>
    /// Seeds an indexed view <c>v</c> over base table <c>b</c>.
    /// </summary>
    private static Simulation IndexedView()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table b (a int not null primary key, c int not null)",
            "create view v with schemabinding as select a, c, count_big(*) as n from dbo.b group by a, c",
            "create unique clustered index ix on v (a, c)",
            "insert b values (1, 1)");
        return simulation;
    }

    /// <summary>
    /// Reading an indexed view through <c>NOEXPAND</c> uses its materialized
    /// index, which the gate covers under the enclosing statement's verb; the
    /// same reference without the hint is never gated (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void SelectNoExpandFromIndexedView_UnderOff_Raises1934()
        => IndexedView().AssertSqlError("set quoted_identifier off; select a from v with (noexpand)", 1934, Msg1934("SELECT"));

    [TestMethod]
    public void SelectNoExpandFromIndexedView_UnderOff_TakesTheEnclosingStatementsVerb()
        => IndexedView().AssertSqlError("""
            set quoted_identifier off;
            declare @t table (a int);
            insert @t select a from v with (noexpand)
            """, 1934, Msg1934("INSERT"));

    [TestMethod]
    public void SelectNoExpandFromIndexedView_UnderWrongAnsiWarnings_Raises1934()
        => IndexedView().AssertSqlError("set ansi_warnings off; select a from v with (noexpand)", 1934, Msg1934("SELECT", "ANSI_WARNINGS"));

    [TestMethod]
    public void SelectWithoutNoExpandFromIndexedView_UnderOff_Succeeds()
        => AreEqual(1, IndexedView().ExecuteScalar("set quoted_identifier off; select a from v"));

    /// <summary>
    /// The hint on a view with no index is not gated — there is no
    /// materialized expression for the setting to disagree with.
    /// </summary>
    [TestMethod]
    public void SelectNoExpandFromPlainView_UnderOff_Succeeds()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table b (a int not null primary key)",
            "create view v as select a from dbo.b",
            "insert b values (1)");
        AreEqual(1, simulation.ExecuteScalar("set quoted_identifier off; select a from v with (noexpand)"));
    }

    // ----- Msg 1934: the non-QUOTED_IDENTIFIER components -----

    /// <summary>
    /// A table whose writes the gate covers, for the option-list tests below.
    /// </summary>
    private static Simulation GatedTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int primary key, b as a * 2 persisted)");
        return simulation;
    }

    [TestMethod]
    public void Insert_UnderAnsiWarningsOff_Raises1934NamingThatOption()
        => GatedTable().AssertSqlError("set ansi_warnings off; insert t (a) values (1)", 1934, Msg1934("INSERT", "ANSI_WARNINGS"));

    [TestMethod]
    public void Insert_UnderNumericRoundabortOn_Raises1934NamingThatOption()
        => GatedTable().AssertSqlError("set numeric_roundabort on; insert t (a) values (1)", 1934, Msg1934("INSERT", "NUMERIC_ROUNDABORT"));

    /// <summary>
    /// Real lists every offending option in one message, in a fixed order that
    /// is neither the order they were set nor alphabetical (probe-confirmed
    /// with three and with five wrong at once).
    /// </summary>
    [TestMethod]
    public void Insert_UnderThreeWrongOptions_ListsThemInRealsOrder()
        => GatedTable().AssertSqlError("""
            set concat_null_yields_null off;
            set ansi_padding off;
            set ansi_nulls off;
            insert t (a) values (1)
            """, 1934, Msg1934("INSERT", "ANSI_NULLS, CONCAT_NULL_YIELDS_NULL, ANSI_PADDING"));

    [TestMethod]
    public void Insert_UnderAllFiveWrongOptions_ListsThemInRealsOrder()
        => GatedTable().AssertSqlError("""
            set ansi_nulls off;
            set ansi_warnings off;
            set numeric_roundabort on;
            set ansi_padding off;
            set concat_null_yields_null off;
            insert t (a) values (1)
            """, 1934, Msg1934("INSERT", "ANSI_NULLS, CONCAT_NULL_YIELDS_NULL, ANSI_WARNINGS, ANSI_PADDING, NUMERIC_ROUNDABORT"));

    /// <summary>
    /// QUOTED_IDENTIFIER off is reported alone whatever the other five say
    /// (probe-confirmed with all six wrong at once).
    /// </summary>
    [TestMethod]
    public void Insert_UnderQuotedIdentifierOffAndOthersWrong_ReportsQuotedIdentifierAlone()
        => GatedTable().AssertSqlError("""
            set quoted_identifier off;
            set ansi_nulls off;
            set numeric_roundabort on;
            insert t (a) values (1)
            """, 1934, Msg1934("INSERT"));

    /// <summary>
    /// ARITHABORT never appears: real accepts a session whose ARITHABORT bit is
    /// 0 as long as ANSI_WARNINGS is on (probe-confirmed by reading
    /// <c>@@OPTIONS &amp; 64</c> inside the accepted batch).
    /// </summary>
    [TestMethod]
    public void Insert_UnderArithabortOff_IsNotGated()
        => AreEqual(1, GatedTable().ExecuteScalar("set arithabort off; insert t (a) values (1); select count(*) from t"));

    [TestMethod]
    public void CreateTable_WithPersistedComputedColumn_UnderAnsiWarningsOff_Raises1934()
        => new Simulation().AssertSqlError(
            "set ansi_warnings off; create table t (a int primary key, b as a * 2 persisted)",
            1934,
            Msg1934("CREATE TABLE", "ANSI_WARNINGS"));

    [TestMethod]
    public void XmlValueMethod_UnderNumericRoundabortOn_Raises1934()
        => new Simulation().AssertSqlError(
            "set numeric_roundabort on; declare @x xml = '<a>1</a>'; select @x.value('(/a)[1]', 'int')",
            1934,
            Msg1934("SELECT", "NUMERIC_ROUNDABORT"));
}
