using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>OBJECT_ID(name [, type])</c> scalar function. Returns the
/// table's stable per-database int id (assigned at CREATE, fresh on
/// DROP-then-recreate) or NULL when not found / wrong type / malformed name.
/// Probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class ObjectIdTests
{
    [TestMethod]
    public void ObjectId_ExistingTable_ReturnsInt()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as id
            """);
        IsTrue(reader.Read());
        AreEqual(typeof(int), reader.GetFieldType(0));
        IsFalse(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_MissingTable_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select object_id('nope') as id");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_NullName_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select object_id(null) as id");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_NullType_ReturnsNull()
    {
        // Probe-confirmed: a NULL type filter propagates NULL even when the
        // table exists — real SQL Server treats it as "no match".
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', null) as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_UserType_Matches()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'U') as id
            """));

    [TestMethod]
    public void ObjectId_UserTypeLowercase_Matches()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'u') as id
            """));

    /// <summary>
    /// The filter is compared against the object's <c>char(2)</c> type code, so
    /// ANSI padding decides which whitespace matters: a one-character code
    /// tolerates the single padding space, a third character never fits, and a
    /// leading space or a tab is a different <c>char(2)</c> value. All four
    /// probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("'U '", true)]
    [DataRow("'U  '", false)]
    [DataRow("' U'", false)]
    [DataRow("'U' + char(9)", false)]
    public void ObjectId_TypeFilterPadding_FollowsCharTwoComparison(string filter, bool resolves)
    {
        using var reader = new Simulation().ExecuteReader($"""
            create table foo (id int);
            select object_id('foo', {filter}) as id
            """);
        IsTrue(reader.Read());
        AreEqual(resolves, !reader.IsDBNull(0));
    }

    /// <summary>
    /// A two-character code fills the <c>char(2)</c> exactly, so it takes no
    /// padding space — <c>'PK '</c> is three characters and matches nothing
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("'PK'", true)]
    [DataRow("'PK '", false)]
    public void ObjectId_TwoCharacterTypeFilter_TakesNoPadding(string filter, bool resolves)
    {
        using var reader = new Simulation().ExecuteReader($"""
            create table foo (id int constraint pk_foo primary key);
            select object_id('pk_foo', {filter}) as id
            """);
        IsTrue(reader.Read());
        AreEqual(resolves, !reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_WrongType_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'V') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_InvalidType_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo', 'XX') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_TwoPartName_Works()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create schema audit;
            create table audit.bar (id int);
            select object_id('audit.bar', 'U') as id
            """));

    [TestMethod]
    public void ObjectId_UnqualifiedResolvesToDbo()
        => AreEqual(new Simulation().ExecuteScalar("""
            create table dbo.foo (id int);
            select object_id('dbo.foo') as id
            """), new Simulation().ExecuteScalar("""
            create table dbo.foo (id int);
            select object_id('foo') as id
            """));

    [TestMethod]
    public void ObjectId_BracketedName_Works()
    {
        var bareId = (int)new Simulation().ExecuteScalar("""
            create table foo (id int);
            select object_id('foo') as id
            """)!;
        var bracketedId = (int)new Simulation().ExecuteScalar("""
            create table foo (id int);
            select object_id('[dbo].[foo]') as id
            """)!;
        // Same Simulation would be more direct but our infra creates two —
        // assert both are non-null ints. The cross-Simulation values differ.
        AreNotEqual(0, bareId);
        AreNotEqual(0, bracketedId);
    }

    [TestMethod]
    public void ObjectId_BracketsAndDots_SingleSimulation()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as a, object_id('[dbo].[foo]') as b, object_id('dbo.foo') as c
            """);
        IsTrue(reader.Read());
        var a = reader.GetInt32(0);
        AreEqual(a, reader.GetInt32(1));
        AreEqual(a, reader.GetInt32(2));
    }

    [TestMethod]
    public void ObjectId_ThreePartCorrectDb_Works()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('simulated.dbo.foo', 'U') as id
            """));

    [TestMethod]
    public void ObjectId_ThreePartWrongDb_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('baddb.dbo.foo', 'U') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_FourPartName_ReturnsNull()
    {
        // Linked-server names aren't modeled — real SQL Server also returns
        // NULL for a 4-part name pointing at a non-existent linked server.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('linked.simulated.dbo.foo', 'U') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_StableAcrossCalls()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as a, object_id('foo') as b
            """);
        IsTrue(reader.Read());
        AreEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void ObjectId_DistinctAcrossTables()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            create table bar (id int);
            select object_id('foo') as foo, object_id('bar') as bar
            """);
        IsTrue(reader.Read());
        AreNotEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void ObjectId_FreshAfterDropAndRecreate()
    {
        // Probe-confirmed against SQL Server 2025: drop-then-recreate yields
        // a fresh int id, the prior value is gone.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            declare @before int = object_id('foo');
            drop table foo;
            create table foo (id int);
            select @before as before_id, object_id('foo') as after_id
            """);
        IsTrue(reader.Read());
        AreNotEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void ObjectId_VariableArgument_RuntimeEvaluated()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            declare @n nvarchar(100) = 'foo';
            select object_id(@n) as id
            """);
        IsTrue(reader.Read());
        IsFalse(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_AfterDrop_IsNull()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            drop table foo;
            select object_id('foo') as id
            """);
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_TempTable_Resolves()
    {
        // Divergence from real SQL Server (documented quirk): the simulator
        // routes # leaves to the connection's temp dict regardless of the
        // current db, so OBJECT_ID('#foo') finds the session's temp table
        // directly rather than requiring tempdb..#foo.
        using var reader = new Simulation().ExecuteReader("""
            create table #foo (id int);
            select object_id('#foo') as id
            """);
        IsTrue(reader.Read());
        IsFalse(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_IfExistsIdiom_Works()
    {
        // The dominant real-world use of OBJECT_ID is the safe-drop pattern.
        // Confirm it threads end-to-end against an existing table.
        _ = new Simulation().ExecuteNonQuery("""
            create table foo (id int);
            if object_id('dbo.foo', 'U') is not null drop table foo;
            create table foo (id int)
            """);
    }

    [TestMethod]
    public void ObjectId_IfExistsIdiom_NoExistingTable()
        => _ = new Simulation().ExecuteNonQuery("""
            if object_id('dbo.foo', 'U') is not null drop table foo;
            create table foo (id int)
            """);

    [TestMethod]
    public void ObjectId_EmptyString_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select object_id('') as id");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectId_TempdbDotDotHash_Resolves()
        => IsFalseDbNull(new Simulation().ExecuteReader("""
            create table #foo (id int);
            select object_id('tempdb..#foo') as id
            """));

    [TestMethod]
    public void ObjectId_ResultTypeIsInt32()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select object_id('foo') as id
            """);
        IsTrue(reader.Read());
        AreEqual(typeof(int), reader.GetFieldType(0));
    }

    [TestMethod]
    public void ObjectId_TooManyArgs_RaisesMsg174()
        => new Simulation().AssertSqlError("select object_id('foo', 'U', 'extra')", 174);

    [TestMethod]
    public void ObjectId_Trigger_NoFilter_Resolves()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int)",
            "create trigger dbo.trg1 on dbo.t after insert as select 1");
        IsFalseDbNull(sim.ExecuteReader("select object_id('dbo.trg1') as id"));
    }

    [TestMethod]
    public void ObjectId_Trigger_TrFilter_Resolves()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int)",
            "create trigger dbo.trg1 on dbo.t after insert as select 1");
        IsFalseDbNull(sim.ExecuteReader("select object_id('dbo.trg1', 'TR') as id"));
    }

    /// <summary>
    /// A constraint name resolves like any other schema-scoped object —
    /// probe-confirmed for a DEFAULT, a CHECK and a PRIMARY KEY, qualified or
    /// not, with the type filter honored and OBJECT_NAME reading back.
    /// </summary>
    [TestMethod]
    [DataRow("object_id('df1')")]
    [DataRow("object_id('dbo.df1')")]
    [DataRow("object_id('[dbo].[df1]')")]
    [DataRow("object_id('df1', 'D')")]
    [DataRow("object_id('ck1')")]
    [DataRow("object_id('ck1', 'C')")]
    [DataRow("object_id('pk1')")]
    [DataRow("object_id('pk1', 'PK')")]
    [DataRow("object_id('uq1', 'UQ')")]
    [DataRow("object_id('fk1', 'F')")]
    public void ObjectId_ConstraintName_Resolves(string expression)
        => IsFalseDbNull(new Simulation().ExecuteReader($"""
            create table parent (id int not null constraint pkp primary key);
            create table t (
                id int not null constraint pk1 primary key,
                u int not null constraint uq1 unique,
                a int constraint df1 default 0,
                p int not null constraint fk1 references parent (id),
                constraint ck1 check (a >= 0));
            select {expression} as id
            """));

    /// <summary>The type filter rejects a constraint of another family.</summary>
    [TestMethod]
    [DataRow("object_id('df1', 'C')")]
    [DataRow("object_id('ck1', 'D')")]
    [DataRow("object_id('pk1', 'UQ')")]
    [DataRow("object_id('uq1', 'PK')")]
    [DataRow("object_id('df1', 'U')")]
    public void ObjectId_ConstraintName_WrongTypeFilter_ReturnsNull(string expression)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"""
            create table t (
                id int not null constraint pk1 primary key,
                u int not null constraint uq1 unique,
                a int constraint df1 default 0,
                constraint ck1 check (a >= 0));
            select {expression}
            """));

    /// <summary>
    /// A constraint's name is scoped to the schema of the table that owns it,
    /// so one on a table in another schema needs qualification (probe-confirmed
    /// against a constraint on a table in a non-default schema).
    /// </summary>
    [TestMethod]
    public void ObjectId_ConstraintInOtherSchema_NeedsQualification()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create schema s",
            "create table s.t (id int not null constraint ck_s check (id > 0))");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('ck_s')"));
        IsFalseDbNull(sim.ExecuteReader("select object_id('s.ck_s') as id"));
    }

    /// <summary>
    /// OBJECT_NAME / OBJECT_SCHEMA_NAME read a constraint id back, and
    /// sys.objects carries the matching row.
    /// </summary>
    [TestMethod]
    public void ObjectName_ConstraintId_ReadsBack()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, a int constraint df1 default 0)");
        AreEqual("df1", sim.ExecuteScalar("select object_name(object_id('df1'))"));
        AreEqual("dbo", sim.ExecuteScalar("select object_schema_name(object_id('df1'))"));
        AreEqual("DEFAULT_CONSTRAINT", sim.ExecuteScalar(
            "select type_desc from sys.objects where object_id = object_id('df1')"));
        AreEqual("t", sim.ExecuteScalar(
            "select object_name(parent_object_id) from sys.objects where object_id = object_id('df1')"));
    }

    // === The per-statement freeze ===
    //
    // A call whose arguments read no column answers the same thing for every
    // row, so it is evaluated once per statement — as real does, where it is a
    // constant scalar in the plan. Resolving a name that matches nothing is
    // the expensive direction (a hit stops at the first dictionary holding it;
    // a miss walks every schema's objects and every table's constraints), so
    // re-running it per row made the ubiquitous
    // `WHERE object_id = OBJECT_ID(N'[dbo].[missing]')` guard quadratic. These
    // pin the freeze's *boundaries*, which is where it could go wrong.

    [TestMethod]
    public void ObjectIdFreeze_DoesNotOutliveTheStatement()
    {
        // The object appears between two statements of the same batch, so the
        // second must not be served the first's NULL.
        var sim = new Simulation();
        using var reader = sim.CreateOpenConnection().CreateCommand("""
            select object_id('freeze_t') as before_create;
            create table freeze_t (a int);
            select object_id('freeze_t') as after_create;
            """).ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        IsFalse(reader.IsDBNull(0));
    }

    [TestMethod]
    public void ObjectIdFreeze_DoesNotOutliveALoopIteration()
    {
        // A WHILE body re-dispatches its statements, so each pass re-resolves.
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("""
            declare @seen int = 0, @i int = 0;
            while @i < 2
            begin
                if object_id('loop_t') is not null set @seen = 1;
                if @i = 0 create table loop_t (a int);
                set @i = @i + 1;
            end
            select @seen;
            """));
    }

    [TestMethod]
    public void ObjectIdOfAColumn_IsNotFrozen()
    {
        // The argument reads a column, so the call is row-dependent and each
        // row has to resolve its own name — freezing here would answer every
        // row with the first one's id.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table freeze_a (x int); create table freeze_b (x int);
            create table names (n sysname);
            insert names values ('freeze_a'), ('freeze_b');
            """);
        AreEqual(2, sim.ExecuteScalar("select count(distinct object_id(n)) from names"));
    }

    [TestMethod]
    public void ObjectIdOfAMissingName_StillAnswersNullEveryRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table freeze_rows (x int); insert freeze_rows values (1), (2), (3)");
        AreEqual(3, sim.ExecuteScalar(
            "select count(*) from freeze_rows where object_id(N'[dbo].[no_such_object]') is null"));
        AreEqual(0, sim.ExecuteScalar(
            "select count(*) from freeze_rows where object_id(N'[dbo].[no_such_object]') is not null"));
    }

    private static void IsFalseDbNull(DbDataReader reader)
    {
        using (reader)
        {
            IsTrue(reader.Read());
            IsFalse(reader.IsDBNull(0));
        }
    }
}
