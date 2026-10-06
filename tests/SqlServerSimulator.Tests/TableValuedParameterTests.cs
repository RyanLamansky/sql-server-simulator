using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Tests for table-valued parameters: <c>CREATE TYPE … AS TABLE</c> /
/// <c>DROP TYPE</c>, <c>DECLARE @t MyType</c>, <c>CREATE PROCEDURE @p
/// MyType READONLY</c>, EXEC with TVP arg from SQL, ADO.NET Structured
/// parameter binding (DataTable / IDataReader). Probed against SQL Server
/// 2025 (2026-05-12).
/// </summary>
[TestClass]
public sealed class TableValuedParameterTests
{
    public TestContext TestContext { get; set; } = null!;

    // ---- CREATE TYPE / DROP TYPE ----

    [TestMethod]
    public void CreateType_Basic_Succeeds()
        => AreEqual(0, WithType("create type dbo.t1 as table (id int, v int)").ExecuteScalar(
            "declare @x dbo.t1; select count(*) from @x"));

    [TestMethod]
    public void CreateType_DuplicateName_RaisesMsg219()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        simulation.AssertSqlError(
            "create type dbo.t1 as table (v int)",
            219,
            "The type 'dbo.t1' already exists, or you do not have permission to create it.");
    }

    [TestMethod]
    public void CreateType_NamedConstraint_RaisesMsg156()
        => new Simulation().AssertSqlError(
            "create type dbo.t1 as table (id int constraint pk_x primary key)",
            156);

    [TestMethod]
    public void CreateType_References_RaisesMsg156()
        => new Simulation().AssertSqlError(
            "create table dbo.parent (id int primary key); create type dbo.t1 as table (id int references dbo.parent(id))",
            156);

    [TestMethod]
    public void DropType_Existing_Succeeds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        _ = simulation.ExecuteNonQuery("drop type dbo.t1");
    }

    [TestMethod]
    public void DropType_Missing_RaisesMsg218()
        => new Simulation().AssertSqlError(
            "drop type dbo.bogus",
            218,
            "Could not find the type 'dbo.bogus'. Either it does not exist or you do not have the necessary permission.");

    [TestMethod]
    public void DropType_MissingIfExists_Succeeds()
        => new Simulation().ExecuteNonQuery("drop type if exists dbo.bogus");

    [TestMethod]
    public void DropType_Referenced_RaisesMsg3732()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create type dbo.t1 as table (id int)",
            "create proc dbo.p1 @rows dbo.t1 readonly as select 1");
        simulation.AssertSqlError(
            "drop type dbo.t1",
            3732,
            "Cannot drop type 'dbo.t1' because it is being referenced by object 'p1'. There may be other objects that reference this type.");
    }

    // ---- DECLARE @t MyType ----

    [TestMethod]
    public void Declare_TwoPartName_Works()
        => AreEqual(2, WithType("create type dbo.t1 as table (id int)").ExecuteScalar(
            "declare @t dbo.t1; insert @t values (1), (2); select count(*) from @t"));

    [TestMethod]
    public void Declare_OnePartName_Works()
        => AreEqual(5, WithType("create type dbo.t1 as table (id int)").ExecuteScalar(
            "declare @t t1; insert @t values (5); select id from @t"));

    [TestMethod]
    public void Declare_UnknownType_RaisesMsg2715()
        => new Simulation().AssertSqlError(
            "declare @t dbo.no_such_type",
            2715);

    [TestMethod]
    public void Declare_PrimaryKey_Inherited_RaisesMsg2627()
        => WithType("create type dbo.t1 as table (id int primary key, v int)").AssertSqlError(
            "declare @t dbo.t1; insert @t values (1, 10), (1, 20)",
            2627);

    [TestMethod]
    public void Declare_Identity_Inherited_AutoIncrements()
        => AreEqual(2, WithType("create type dbo.t1 as table (id int identity(1,1), v int)").ExecuteScalar(
            "declare @t dbo.t1; insert @t (v) values (10), (20); select id from @t order by id offset 1 rows fetch next 1 rows only"));

    [TestMethod]
    public void Declare_Check_Inherited_RaisesMsg547()
        => WithType("create type dbo.t1 as table (id int, v int check (v > 0))").AssertSqlError(
            "declare @t dbo.t1; insert @t values (1, -5)",
            547);

    [TestMethod]
    public void Declare_Computed_Inherited_Works()
        => AreEqual(7, WithType("create type dbo.t1 as table (a int, b int, c as a+b)").ExecuteScalar(
            "declare @t dbo.t1; insert @t (a, b) values (3, 4); select c from @t"));

    [TestMethod]
    public void Declare_RowVersion_Inherited_AdvancesAcrossInserts()
    {
        var rv1 = WithType("create type dbo.t1 as table (id int, rv rowversion)").ExecuteScalar("""
            declare @t dbo.t1;
            insert @t (id) values (1), (2);
            select max(cast(rv as bigint)) - min(cast(rv as bigint)) from @t
            """);
        AreEqual(1L, rv1);
    }

    [TestMethod]
    public void Declare_SetIdentityInsert_RaisesMsg102()
        => WithType("create type dbo.t1 as table (id int identity)").AssertSqlError(
            "declare @t dbo.t1; set identity_insert @t on",
            102);

    [TestMethod]
    public void Declare_MultiVar_WithTvpTypes_Works()
        => WithType("create type dbo.t1 as table (id int)").ExecuteNonQuery(
            "declare @t1 dbo.t1, @t2 dbo.t1");

    // ---- CREATE PROC TVP parameter ----

    [TestMethod]
    public void CreateProc_TvpParam_WithReadOnly_Works()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create type dbo.t1 as table (id int)",
            "create proc dbo.p1 @rows dbo.t1 readonly as select count(*) c from @rows");
    }

    [TestMethod]
    public void CreateProc_TvpParam_MissingReadOnly_RaisesMsg352()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        simulation.AssertSqlError(
            "create proc dbo.p1 @rows dbo.t1 as select 1",
            352,
            "The table-valued parameter \"@rows\" must be declared with the READONLY option.");
    }

    [TestMethod]
    public void CreateProc_TvpParam_WithDefault_RaisesMsg102()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        _ = simulation.AssertSqlError("create proc dbo.p1 @rows dbo.t1 readonly = null as select 1", 102);
    }

    [TestMethod]
    public void CreateProc_TvpParam_WithOutput_RaisesMsg102()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        _ = simulation.AssertSqlError("create proc dbo.p1 @rows dbo.t1 readonly output as select 1", 102);
    }

    [TestMethod]
    public void ProcBody_InsertTvpParam_RaisesMsg10700()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        // The CREATE binds the body, so the write is caught there and the
        // procedure never lands (probe-confirmed against SQL Server 2025).
        simulation.AssertSqlError(
            "create proc dbo.p1 @rows dbo.t1 readonly as insert @rows values (99)",
            10700,
            "The table-valued parameter \"@rows\" is READONLY and cannot be modified.");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.procedures where name = 'p1'"));
    }

    [TestMethod]
    public void ProcBody_UpdateTvpParam_RaisesMsg10700()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        _ = simulation.AssertSqlError(
            "create proc dbo.p1 @rows dbo.t1 readonly as update @rows set id = 0",
            10700);
    }

    [TestMethod]
    public void ProcBody_DeleteTvpParam_RaisesMsg10700()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        _ = simulation.AssertSqlError(
            "create proc dbo.p1 @rows dbo.t1 readonly as delete @rows",
            10700);
    }

    // ---- EXEC with TVP arg from SQL ----

    // NB: CREATE TYPE / CREATE PROC are split into separate batches because
    // CREATE PROCEDURE must be the first statement in a batch (Msg 111).

    private static Simulation CreateSimWithProc(string procBody)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type dbo.t1 as table (id int)",
            $"create proc dbo.p1 @rows dbo.t1 readonly as {procBody}");
        return sim;
    }

    [TestMethod]
    public void Exec_TvpArg_NamedForm_PassesRows()
        => AreEqual(2, CreateSimWithProc("select count(*) c from @rows").ExecuteScalar(
            "declare @t dbo.t1; insert @t values (1), (2); exec dbo.p1 @rows = @t"));

    [TestMethod]
    public void Exec_TvpArg_PositionalForm_PassesRows()
        => AreEqual(3, CreateSimWithProc("select count(*) c from @rows").ExecuteScalar(
            "declare @t dbo.t1; insert @t values (10), (20), (30); exec dbo.p1 @t"));

    [TestMethod]
    public void Exec_TvpArg_Empty_PassesZeroRows()
        => AreEqual(0, CreateSimWithProc("select count(*) c from @rows").ExecuteScalar(
            "declare @t dbo.t1; exec dbo.p1 @t"));

    [TestMethod]
    public void Exec_TvpArg_OmittedEntirely_PassesEmptyTable()
        => AreEqual(0, CreateSimWithProc("select count(*) c from @rows").ExecuteScalar("exec dbo.p1"));

    [TestMethod]
    public void Exec_TvpArg_ScalarValue_RaisesMsg206()
        => CreateSimWithProc("select 1").AssertSqlError("exec dbo.p1 @rows = 5", 206);

    [TestMethod]
    public void Exec_TvpArg_NestedProcPassThrough_PreservesRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        _ = sim.ExecuteNonQuery("create proc dbo.inner_p @r dbo.t1 readonly as select count(*) c from @r");
        _ = sim.ExecuteNonQuery("create proc dbo.outer_p @r dbo.t1 readonly as exec dbo.inner_p @r");
        AreEqual(2, sim.ExecuteScalar("declare @t dbo.t1; insert @t values (1), (2); exec dbo.outer_p @t"));
    }

    // ---- Off-row values crossing the proc-parameter copy ----

    // A table variable's off-row bytes (LOB chains for MAX-typed columns,
    // and bounded var columns overflow-pushed past 8060) live in the source
    // heap; the proc-parameter copy must re-home them into the parameter's
    // own heap or the copied rows' pointers dangle.

    [TestMethod]
    public void Exec_TvpArg_NvarcharMaxColumn_LobValueSurvivesParameterCopy()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type dbo.t1 as table (id int, doc nvarchar(max))",
            "create proc dbo.p1 @rows dbo.t1 readonly as select cast(len(doc) as int) l, substring(doc, 99999, 2) tail from @rows");
        using var rdr = sim.ExecuteReader("""
            declare @t dbo.t1;
            insert @t values (1, replicate(cast(N'x' as nvarchar(max)), 99999) + N'y');
            exec dbo.p1 @t
            """);
        IsTrue(rdr.Read());
        AreEqual(100000, rdr.GetInt32(0));
        AreEqual("xy", rdr.GetString(1));
    }

    [TestMethod]
    public void Exec_TvpArg_OverflowPushedBoundedColumn_SurvivesParameterCopy()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type dbo.t1 as table (id int, a varchar(8000), b varchar(8000))",
            "create proc dbo.p1 @rows dbo.t1 readonly as select len(a) + len(b) from @rows");
        AreEqual(16000, sim.ExecuteScalar("""
            declare @t dbo.t1;
            insert @t values (1, replicate('a', 8000), replicate('b', 8000));
            exec dbo.p1 @t
            """));
    }

    [TestMethod]
    public void Structured_DataTable_NvarcharMaxColumn_LobValueSurvivesParameterCopy()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int, doc nvarchar(max))");
        _ = simulation.ExecuteNonQuery("create proc dbo.p1 @rows dbo.t1 readonly as select cast(len(doc) as int) from @rows");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "exec dbo.p1 @rows";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        _ = dt.Columns.Add("doc", typeof(string));
        _ = dt.Rows.Add(1, new string('x', 100000));
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        AreEqual(100000, cmd.ExecuteScalar());
    }

    // ---- ADO.NET Structured parameter (DataTable / IDataReader) ----

    [TestMethod]
    public void Structured_DataTable_BindsAndPassesPositionally()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int, v int)");
        _ = simulation.ExecuteNonQuery("create proc dbo.p1 @rows dbo.t1 readonly as select id, v from @rows order by id");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "exec dbo.p1 @rows";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        _ = dt.Columns.Add("v", typeof(int));
        _ = dt.Rows.Add(1, 100);
        _ = dt.Rows.Add(2, 200);
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        using var rdr = cmd.ExecuteReader();
        IsTrue(rdr.Read());
        AreEqual(1, rdr.GetInt32(0));
        AreEqual(100, rdr.GetInt32(1));
        IsTrue(rdr.Read());
        AreEqual(2, rdr.GetInt32(0));
        AreEqual(200, rdr.GetInt32(1));
        IsFalse(rdr.Read());
    }

    /// <summary>
    /// A TVP row's cells are converted by the destination column's own type,
    /// so a table type carrying <c>hierarchyid</c>, <c>geography</c> and
    /// <c>smalldatetime</c> reaches each of those conversions — the path a
    /// plain scalar parameter never takes, since none of the three maps from
    /// a <see cref="DbType"/>.
    /// </summary>
    [TestMethod]
    public void Structured_DataTable_ConvertsPerDestinationColumnType()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (h hierarchyid, g geography, w smalldatetime)");
        _ = simulation.ExecuteNonQuery(
            "create proc dbo.p1 @rows dbo.t1 readonly as select h.ToString(), g.STAsText(), w from @rows");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "exec dbo.p1 @rows";
        var dt = new DataTable();
        _ = dt.Columns.Add("h", typeof(string));
        _ = dt.Columns.Add("g", typeof(string));
        _ = dt.Columns.Add("w", typeof(DateTime));
        _ = dt.Rows.Add("/1/2/", "POINT (-122 47)", new DateTime(2024, 3, 4, 5, 6, 0, DateTimeKind.Unspecified));
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        using var rdr = cmd.ExecuteReader();
        IsTrue(rdr.Read());
        AreEqual("/1/2/", rdr.GetString(0));
        AreEqual("POINT (-122 47)", rdr.GetString(1));
        AreEqual(new DateTime(2024, 3, 4, 5, 6, 0, DateTimeKind.Unspecified), rdr.GetDateTime(2));
        IsFalse(rdr.Read());
    }

    [TestMethod]
    public void Structured_DataTable_ColumnNamesIgnored_PositionalFill()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int, v int)");
        _ = simulation.ExecuteNonQuery("create proc dbo.p1 @rows dbo.t1 readonly as select id, v from @rows");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "exec dbo.p1 @rows";
        // Column names "foo"/"bar" don't match the type's "id"/"v" — should
        // still bind positionally (probe F2 confirmed).
        var dt = new DataTable();
        _ = dt.Columns.Add("foo", typeof(int));
        _ = dt.Columns.Add("bar", typeof(int));
        _ = dt.Rows.Add(42, 99);
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        using var rdr = cmd.ExecuteReader();
        IsTrue(rdr.Read());
        AreEqual(42, rdr.GetInt32(0));
        AreEqual(99, rdr.GetInt32(1));
    }

    [TestMethod]
    public void Structured_DataTable_WrongColumnCount_RaisesMsg500()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int, v int)");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "create proc dbo.p1 @rows dbo.t1 readonly as select 1";
        _ = cmd.ExecuteNonQuery();

        cmd.CommandText = "exec dbo.p1 @rows";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        _ = dt.Rows.Add(1);
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        var ex = Throws<SimulatedSqlException>(() => cmd.ExecuteNonQuery());
        AreEqual(500, ex.Number);
    }

    [TestMethod]
    public void Structured_DataTable_MissingTypeName_Throws()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select 1";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        // intentionally no TypeName
        _ = cmd.Parameters.Add(p);

        _ = Throws<ArgumentException>(() => cmd.ExecuteNonQuery());
    }

    [TestMethod]
    public void Structured_DataTable_UnknownTypeName_RaisesMsg2715()
    {
        var simulation = new Simulation();

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select 1";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.no_such_type";
        _ = cmd.Parameters.Add(p);

        var ex = Throws<SimulatedSqlException>(() => cmd.ExecuteNonQuery());
        AreEqual(2715, ex.Number);
    }

    [TestMethod]
    public void Structured_DataTable_IdentityValueSupplied_RaisesMsg1077()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int identity(1,1), v int)");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select 1";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        _ = dt.Columns.Add("v", typeof(int));
        _ = dt.Rows.Add(100, 1);  // explicit identity value — probe F8 → Msg 1077
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        var ex = Throws<SimulatedSqlException>(() => cmd.ExecuteNonQuery());
        AreEqual(1077, ex.Number);
    }

    [TestMethod]
    public void Structured_DataTable_NullIntoNotNullColumn_RaisesMsg515()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int not null, v int)");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select id from @rows";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        _ = dt.Columns.Add("v", typeof(int));
        _ = dt.Rows.Add(DBNull.Value, 1);
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        var ex = Throws<SimulatedSqlException>(() => cmd.ExecuteNonQuery());
        AreEqual(515, ex.Number);
    }

    [TestMethod]
    public void Structured_DataTable_DuplicatePrimaryKey_RaisesMsg2627()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int not null primary key, v int)");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select id from @rows";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        _ = dt.Columns.Add("v", typeof(int));
        _ = dt.Rows.Add(1, 10);
        _ = dt.Rows.Add(1, 20);
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        var ex = Throws<SimulatedSqlException>(() => cmd.ExecuteNonQuery());
        AreEqual(2627, ex.Number);
    }

    [TestMethod]
    public void Structured_DataTable_CheckViolation_RaisesMsg547()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int not null check (id > 0))");

        using var con = simulation.CreateDbConnection();
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select id from @rows";
        var dt = new DataTable();
        _ = dt.Columns.Add("id", typeof(int));
        _ = dt.Rows.Add(-5);
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = dt;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        var ex = Throws<SimulatedSqlException>(() => cmd.ExecuteNonQuery());
        AreEqual(547, ex.Number);
    }

    [TestMethod]
    public void Structured_IDataReader_BindsViaCommand()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int, v int)");
        _ = simulation.ExecuteNonQuery("create proc dbo.p1 @rows dbo.t1 readonly as select id, v from @rows order by id");

        using var con = simulation.CreateDbConnection();
        con.Open();

        // Build the source rows in a regular table; the reader from a SELECT
        // is the IDataReader we'll bind.
        using (var setup = con.CreateCommand())
        {
            setup.CommandText = "create table dbo.src (id int, v int); insert dbo.src values (7, 70), (8, 80)";
            _ = setup.ExecuteNonQuery();
        }

        // Open a second connection for the source reader so the parameter
        // binding can iterate without colliding with the parent command's
        // active reader.
        using var srcCon = simulation.CreateDbConnection();
        srcCon.Open();
        using var srcCmd = srcCon.CreateCommand();
        srcCmd.CommandText = "select id, v from dbo.src order by id";
        using var srcReader = srcCmd.ExecuteReader();

        using var cmd = con.CreateCommand();
        cmd.CommandText = "exec dbo.p1 @rows";
        var p = cmd.CreateParameter();
        p.ParameterName = "@rows";
        p.Value = srcReader;
        p.TypeName = "dbo.t1";
        _ = cmd.Parameters.Add(p);

        using var rdr = cmd.ExecuteReader();
        IsTrue(rdr.Read());
        AreEqual(7, rdr.GetInt32(0));
        AreEqual(70, rdr.GetInt32(1));
        IsTrue(rdr.Read());
        AreEqual(8, rdr.GetInt32(0));
        AreEqual(80, rdr.GetInt32(1));
    }

    // ---- TypeName property ----

    [TestMethod]
    public void TypeName_DefaultsToEmptyString()
    {
        var simulation = new Simulation();
        using var con = simulation.CreateDbConnection();
        using var cmd = con.CreateCommand();
        var p = cmd.CreateParameter();
        AreEqual("", p.TypeName);
    }

    [TestMethod]
    public void TypeName_SetThenGet_RoundTrips()
    {
        var simulation = new Simulation();
        using var con = simulation.CreateDbConnection();
        using var cmd = con.CreateCommand();
        var p = cmd.CreateParameter();
        p.TypeName = "dbo.MyType";
        AreEqual("dbo.MyType", p.TypeName);
    }

    [TestMethod]
    public void TypeName_SetEmpty_Clears()
    {
        var simulation = new Simulation();
        using var con = simulation.CreateDbConnection();
        using var cmd = con.CreateCommand();
        var p = cmd.CreateParameter();
        p.TypeName = "dbo.MyType";
        p.TypeName = "";
        AreEqual("", p.TypeName);
    }

    // ---- Catalog views ----

    [TestMethod]
    public void SysTypes_IncludesUserDefinedTableType()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        using var rdr = simulation.ExecuteReader(
            "select name, system_type_id, is_user_defined, is_table_type from sys.types where name = 'dbo.t1' or name = 't1'");
        IsTrue(rdr.Read());
        AreEqual("t1", rdr.GetString(0));
        AreEqual((byte)243, rdr.GetByte(1));
        IsTrue(rdr.GetBoolean(2));
        IsTrue(rdr.GetBoolean(3));
    }

    [TestMethod]
    public void SysTableTypes_HasRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        AreEqual(1, simulation.ExecuteScalar(
            "select count(*) from sys.table_types where name = 't1'"));
    }

    [TestMethod]
    public void SysColumns_ProjectsThroughTableType()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int, v varchar(50))");
        AreEqual(2, simulation.ExecuteScalar("""
            select count(*) from sys.columns c
            join sys.table_types tt on tt.type_table_object_id = c.object_id
            where tt.name = 't1'
            """));
    }

    [TestMethod]
    public void SysParameters_IsReadonlyTrueForTvp()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create type dbo.t1 as table (id int)",
            "create proc dbo.p1 @rows dbo.t1 readonly as select 1");
        IsTrue((bool)simulation.ExecuteScalar(
            "select is_readonly from sys.parameters where object_id = object_id('dbo.p1')")!);
    }

    [TestMethod]
    public void InformationSchemaDomains_HasTableTypeRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        using var rdr = simulation.ExecuteReader(
            "select domain_schema, domain_name, data_type from information_schema.domains where domain_name = 't1'");
        IsTrue(rdr.Read());
        AreEqual("dbo", rdr.GetString(0));
        AreEqual("t1", rdr.GetString(1));
        AreEqual("table type", rdr.GetString(2));
    }

    // ---- TYPE_ID ----

    [TestMethod]
    public void TypeId_UserDefined_ReturnsUserTypeId()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create type dbo.t1 as table (id int)");
        // The exact id depends on allocation order; just assert it's not null
        // and is in the user-type range (>= 256).
        var id = simulation.ExecuteScalar<int>("select type_id('dbo.t1')");
        IsGreaterThanOrEqualTo(256, id);
    }

    [TestMethod]
    public void TypeId_SystemType_ReturnsKnownId()
        => AreEqual(56, new Simulation().ExecuteScalar("select type_id('int')"));

    [TestMethod]
    public void TypeId_Unknown_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select type_id('dbo.bogus')"));

    // --- Table-type column nullability and the backing table's PRIMARY KEY ---

    [TestMethod]
    public void TableTypeColumn_ReportsItsDeclaredNullability()
        => AreEqual("a=0 b=0 c=1", new Simulation().ExecuteScalar("""
            create type dbo.tt as table (a int not null, b nvarchar(50) not null, c int null);
            select string_agg(concat(c.name, '=', c.is_nullable), ' ') within group (order by c.column_id)
            from sys.table_types tt
            join sys.columns c on c.object_id = tt.type_table_object_id
            where tt.name = 'tt'
            """));

    /// <summary>
    /// A table type's PRIMARY KEY lives on its backing type table, which real
    /// homes in the sys schema and reports through sys.key_constraints — the
    /// pair DacFx reads to re-emit the key clause.
    /// </summary>
    [TestMethod]
    public void TableTypePrimaryKey_ReportsThroughKeyConstraints()
        => AreEqual("PK/1", new Simulation().ExecuteScalar("""
            create type dbo.tt as table (a int not null primary key, b int);
            select concat(kc.type, '/', kc.unique_index_id)
            from sys.key_constraints kc
            join sys.table_types tt on tt.type_table_object_id = kc.parent_object_id
            where tt.name = 'tt'
            """));

    [TestMethod]
    public void TableTypePrimaryKey_IsNamedAfterItsBackingTable()
        => StartsWith("PK__TT_tt", (string)new Simulation().ExecuteScalar("""
            create type dbo.tt as table (a int not null primary key, b int);
            select kc.name from sys.key_constraints kc
            join sys.table_types tt on tt.type_table_object_id = kc.parent_object_id
            where tt.name = 'tt'
            """)!);
    // ---- Table-valued parameters of functions and sp_executesql ----

    /// <summary>
    /// A function of every kind and an <c>sp_executesql</c> declaration take a
    /// table-type <c>READONLY</c> parameter, and a call passes a table variable
    /// of that type (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create function f (@t tt readonly) returns table as return select a from @t where a > 1",
        "select string_agg(a, ',') within group (order by a) from dbo.f(@x)", "2,4")]
    [DataRow("create function f (@t tt readonly, @m int) returns table as return select a from @t where a > @m",
        "select string_agg(concat(v, ':', a), ',') within group (order by v, a) from (values (0), (2)) d(v) cross apply dbo.f(@x, v)", "0:1,0:2,0:4,2:4")]
    [DataRow("create function f (@t dbo.tt readonly) returns @r table (n int, s int) as begin insert @r select count(*), sum(a) from @t; return; end",
        "select concat(n, '/', s) from dbo.f(@x)", "3/7")]
    [DataRow("create function f (@t tt readonly) returns int as begin return (select sum(a) from @t); end",
        "select dbo.f(@x) + 1", "8")]
    [DataRow("create function f (@t tt readonly, @m int) returns int as begin return (select count(*) from @t where a > @m); end",
        "select string_agg(concat(v, ':', dbo.f(@x, v)), ',') within group (order by v) from (values (0), (1), (3)) d(v)", "0:3,1:2,3:1")]
    [DataRow("create function f (@t tt readonly) returns int as begin return (select count(*) from @t); end",
        "select dbo.f((@x))", "3")]
    [DataRow("create function f (@t tt readonly) returns int as begin return (select count(*) from @t); end",
        "select dbo.f(default)", "0")]
    [DataRow("create function f (@t tt readonly) returns @r table (n int) as begin insert @r select count(*) from @t; return; end",
        "select n from dbo.f(default)", "0")]
    [DataRow("create procedure p as select 1",
        "exec sp_executesql N'select sum(a) from @p where a > @m', N'@m int, @p tt readonly', @p = @x, @m = 1", "6")]
    [DataRow("create procedure p as select 1",
        "exec sp_executesql N'select count(*) from @p', N'@p dbo.tt readonly', default", "0")]
    public void TableValuedParameter_OutsideProcedures(string module, string call, string expected) =>
        AreEqual(expected, Convert.ToString(new Simulation().ExecuteBatchesScalar(
            "create type tt as table (a int)",
            module,
            "declare @x tt; insert @x values (1), (2), (4); " + call), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A function's body calls another with its own table-valued parameter, as a procedure's does.</summary>
    [TestMethod]
    public void TableValuedParameter_PassesThroughNestedModules() =>
        AreEqual(70, new Simulation().ExecuteBatchesScalar(
            "create type tt as table (a int)",
            "create function g (@t tt readonly) returns int as begin return (select sum(a) from @t); end",
            "create function f (@t tt readonly) returns int as begin return dbo.g(@t) * 10; end",
            "create procedure p @t tt readonly as exec sp_executesql N'select dbo.f(@p)', N'@p tt readonly', @t",
            "declare @x tt; insert @x values (3), (4); exec p @x"));

    /// <summary>
    /// What a call may pass a table-valued parameter: a table variable of the
    /// parameter's own type and nothing else, each refusal Msg 206 naming the
    /// type; an inline function's <c>DEFAULT</c> there is Msg 1090.
    /// </summary>
    [TestMethod]
    [DataRow("select dbo.s(null)", 206, "Operand type clash: NULL is incompatible with tt")]
    [DataRow("select dbo.s(@i)", 206, "Operand type clash: int is incompatible with tt")]
    [DataRow("select dbo.s(@y)", 206, "Operand type clash: tt2 is incompatible with tt")]
    [DataRow("select dbo.s(@z)", 206, "Operand type clash: table is incompatible with tt")]
    [DataRow("select * from dbo.i(null)", 206, "Operand type clash: NULL is incompatible with tt")]
    [DataRow("select * from dbo.i(@y)", 206, "Operand type clash: tt2 is incompatible with tt")]
    [DataRow("select * from dbo.i(default)", 1090, "Invalid default for parameter 1.")]
    [DataRow("exec sp_executesql N'select 1 from @p', N'@p tt readonly', null", 206, "Operand type clash: NULL is incompatible with tt")]
    [DataRow("exec sp_executesql N'select 1 from @p', N'@p tt readonly', 1", 206, "Operand type clash: int is incompatible with tt")]
    [DataRow("exec sp_executesql N'select 1 from @p', N'@p tt readonly', @y", 206, "Operand type clash: tt2 is incompatible with tt")]
    [DataRow("exec sp_executesql N'select 1 from @p', N'@p tt readonly', @z", 206, "Operand type clash: table is incompatible with tt")]
    [DataRow("exec sp_executesql N'select @p', N'@p int', @x", 206, "Operand type clash: tt is incompatible with int")]
    [DataRow("exec sp_executesql N'select 1', N'@p tt', @x", 352, "The table-valued parameter \"@p\" must be declared with the READONLY option.")]
    [DataRow("exec sp_executesql N'delete @p', N'@p tt readonly', @x", 10700, "The table-valued parameter \"@p\" is READONLY and cannot be modified.")]
    public void TableValuedArgument_Refused(string call, int number, string message)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type tt as table (a int)",
            "create type tt2 as table (a int)",
            "create function s (@t tt readonly) returns int as begin return (select count(*) from @t); end",
            "create function i (@t tt readonly) returns table as return select a from @t");
        sim.AssertSqlError("declare @x tt; declare @y tt2; declare @z table (a int); declare @i int = 1; " + call, number, message);
    }

    /// <summary>
    /// An <c>sp_executesql</c> declaration or argument error is the call's
    /// own, so the caller's batch runs on past it.
    /// </summary>
    [TestMethod]
    public void SpExecuteSqlTableValuedParameterError_EndsOnlyTheCall()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type tt as table (a int); create table log (a int)");
        _ = sim.AssertSqlError("exec sp_executesql N'select 1', N'@p tt', 1; insert log values (1)", 352);
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from log"));
    }

    /// <summary>
    /// The declarations a function's CREATE refuses: a missing READONLY, a
    /// default, a body writing the parameter, a one-part type name under
    /// SCHEMABINDING, and INLINE = ON.
    /// </summary>
    [TestMethod]
    [DataRow("create function f (@t tt) returns int as begin return 1; end", 352)]
    [DataRow("create function f (@t tt) returns table as return select 1 x", 352)]
    [DataRow("create function f (@t tt = null readonly) returns int as begin return 1; end", 206)]
    [DataRow("create function f (@t tt readonly) returns @r table (a int) as begin delete @t; return; end", 10700)]
    [DataRow("create function f (@t tt readonly) returns int as begin update @t set a = 1; return 1; end", 10700)]
    [DataRow("create function f (@t tt readonly) returns int with schemabinding as begin return 1; end", 2789)]
    [DataRow("create function f (@t tt readonly) returns table with schemabinding as return select 1 x", 2789)]
    [DataRow("create function f (@t tt readonly) returns int with inline = on as begin return (select count(*) from @t); end", 16203)]
    public void FunctionTableValuedParameter_RefusedAtCreate(string create, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type tt as table (a int)");
        _ = sim.AssertSqlError(create, number);
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select object_id('f')"));
    }

    [TestMethod]
    public void FunctionTableValuedParameter_SchemaBoundTwoPartName_Runs() =>
        AreEqual(2, new Simulation().ExecuteBatchesScalar(
            "create type tt as table (a int)",
            "create function f (@t dbo.tt readonly) returns int with schemabinding as begin return (select count(*) from @t); end",
            "declare @x dbo.tt; insert @x values (1), (2); select dbo.f(@x)"));

    /// <summary>
    /// The catalog lists a function's table-valued parameter as a procedure's:
    /// type 243 of the table type, read-only, a type dependency, and the
    /// function no longer inlineable; and the type can't be dropped under it.
    /// </summary>
    [TestMethod]
    [DataRow("select concat(name, '/', system_type_id, '/', type_name(user_type_id), '/', is_readonly, '/', max_length) from sys.parameters where object_id = object_id('f') and parameter_id = 1", "@t/243/tt/1/-1")]
    [DataRow("select concat(data_type, '/', user_defined_type_name) from information_schema.parameters where specific_name = 'f' and ordinal_position = 1", "table type/tt")]
    [DataRow("select referenced_class_desc from sys.sql_expression_dependencies where referencing_id = object_id('f') and referenced_entity_name = 'tt'", "TYPE")]
    [DataRow("select is_inlineable from sys.sql_modules where object_id = object_id('f')", "False")]
    public void FunctionTableValuedParameter_Catalog(string query, string expected) =>
        AreEqual(expected, Convert.ToString(new Simulation().ExecuteBatchesScalar(
            "create type tt as table (a int)",
            "create function f (@t tt readonly, @m int) returns int as begin return (select count(*) from @t where a > @m); end",
            query), System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    public void DropType_ReferencedByFunction_IsMsg3732()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type tt as table (a int)",
            "create function f (@t tt readonly) returns table as return select a from @t");
        sim.AssertSqlError("drop type tt", 3732, "Cannot drop type 'tt' because it is being referenced by object 'f'. There may be other objects that reference this type.");
    }

    /// <summary>
    /// Msg 3732 ends the batch and rolls back an open transaction, as under
    /// <c>SET XACT_ABORT ON</c>, while a <c>CATCH</c> still takes it (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void DropType_StillReferenced_EndsTheBatch()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        foreach (var batch in new[] { "create type tt as table (a int)", "create procedure p @t tt readonly as select 1" })
            _ = connection.CreateCommand(batch).ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; drop type tt; select 'after'").ExecuteScalar());
        AreEqual(3732, ex.Number);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual("caught 3732|tail", connection.CreateCommand(
            "declare @r varchar(20); begin try drop type tt; set @r = 'after' end try begin catch set @r = 'caught ' + cast(error_number() as varchar(10)) end catch; select @r + '|tail'").ExecuteScalar());
    }
}
