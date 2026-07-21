using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Column-level GRANT / DENY enforcement (Msg 230 / 229). A base-table read
/// requires SELECT on every column it touches; a write requires UPDATE on every
/// assigned column and SELECT on every column it reads; a column DENY overrides
/// a table GRANT; a wholly inaccessible object falls back to the object-level
/// Msg 229. INSERT stays object-grain. Probe-confirmed against SQL Server 2025
/// (dbo.col_t with id/a/b/c and the W1–W5 grants).
/// </summary>
[TestClass]
public sealed class ColumnLevelGrantTests
{
    // visuser holds: GRANT SELECT(id), GRANT SELECT(a), DENY SELECT(c),
    // GRANT UPDATE(b) — and no table-level grant.
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.col_t (id int not null, a int, b int, c int)",
            "insert dbo.col_t values (1, 10, 20, 30)",
            "create user u without login",
            "grant select (id) on dbo.col_t to u",
            "grant select (a) on dbo.col_t to u",
            "deny select (c) on dbo.col_t to u",
            "grant update (b) on dbo.col_t to u");
        return sim;
    }

    [TestMethod]
    public void Select_GrantedColumns_Succeed()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.col_t"));
        AreEqual(10, sim.ExecuteScalar("execute as user = 'u'; select a from dbo.col_t"));
    }

    [TestMethod]
    public void Select_UngrantedColumn_Raises230()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select b from dbo.col_t", 230);
        Contains("SELECT permission was denied on the column 'b'", ex.Message);
        Contains("of the object 'col_t'", ex.Message);
    }

    [TestMethod]
    public void Select_DeniedColumn_Raises230()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select c from dbo.col_t", 230);
        Contains("column 'c'", ex.Message);
    }

    [TestMethod]
    public void ColumnDeny_BeatsTableGrant()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int, b int)",
            "insert dbo.t values (1, 2)",
            "create user u without login",
            "grant select on dbo.t to u",
            "deny select (b) on dbo.t to u");
        // Table grant covers a; the column DENY on b overrides the table grant.
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select a from dbo.t"));
        _ = sim.AssertSqlError("execute as user = 'u'; select b from dbo.t", 230);
    }

    [TestMethod]
    public void SelectStar_OverPartialGrant_Raises230OnFirstOffendingColumn()
    {
        // id (ok), a (ok), b (no SELECT) — b is the first inaccessible column by ordinal.
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select * from dbo.col_t", 230);
        Contains("column 'b'", ex.Message);
    }

    [TestMethod]
    public void CountStar_ChecksEveryColumn_Raises230()
    {
        // COUNT(*) names no column, which real checks as requiring SELECT on all.
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select count(*) from dbo.col_t", 230);
        Contains("column 'b'", ex.Message);
    }

    [TestMethod]
    public void Count_OverGrantedColumn_Succeeds()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select count(id) from dbo.col_t"));
    }

    [TestMethod]
    public void Where_OverDeniedColumn_Raises230()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select id from dbo.col_t where c = 1", 230);
        Contains("column 'c'", ex.Message);
    }

    [TestMethod]
    public void Where_OverGrantedColumn_Succeeds()
    {
        var sim = Seeded();
        // id and a both granted; a=10 matches the single row.
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.col_t where a = 10"));
    }

    [TestMethod]
    public void ZeroAccess_Raises229_NotColumnLevel()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int, a int)",
            "insert dbo.t values (1, 2)",
            "create user u without login");
        // No grant of any kind — the object-level check fails first (Msg 229),
        // even for an explicit column reference.
        var ex = sim.AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
        Contains("SELECT permission was denied on the object 't'", ex.Message);
    }

    [TestMethod]
    public void Update_AssignedColumn_Succeeds()
    {
        var sim = Seeded();
        // UPDATE(b) granted; WHERE reads id (granted).
        _ = sim.ExecuteNonQuery("execute as user = 'u'; update dbo.col_t set b = 99 where id = 1");
        AreEqual(99, sim.ExecuteScalar("select b from dbo.col_t where id = 1"));
    }

    [TestMethod]
    public void Update_UnassignedGrantColumn_Raises230()
    {
        // No UPDATE(a) grant — assigning a is denied at column grain.
        var ex = Seeded().AssertSqlError("execute as user = 'u'; update dbo.col_t set a = 99 where id = 1", 230);
        Contains("UPDATE permission was denied on the column 'a'", ex.Message);
    }

    [TestMethod]
    public void Update_SetReadingColumn_RequiresSelectOnIt()
    {
        // SET b = b + 1 reads b; UPDATE(b) is granted but SELECT(b) is not.
        var ex = Seeded().AssertSqlError("execute as user = 'u'; update dbo.col_t set b = b + 1 where id = 1", 230);
        Contains("SELECT permission was denied on the column 'b'", ex.Message);
    }

    [TestMethod]
    public void Update_ConstantSet_NeedsOnlyUpdateOnTarget()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int, b int)",
            "insert dbo.t values (1, 2)",
            "create user u without login",
            "grant update (b) on dbo.t to u");
        // Constant SET, no WHERE — reads nothing, needs only UPDATE(b).
        _ = sim.ExecuteNonQuery("execute as user = 'u'; update dbo.t set b = 5");
        AreEqual(5, sim.ExecuteScalar("select b from dbo.t"));
    }

    [TestMethod]
    public void Delete_WhereOverDeniedColumn_Raises230()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; delete dbo.col_t where c = 1", 230);
        Contains("SELECT permission was denied on the column 'c'", ex.Message);
    }

    [TestMethod]
    public void Insert_WithoutGrant_Raises229_ObjectGrain()
    {
        // INSERT is object-grain: no INSERT grant → Msg 229 on the object.
        var ex = Seeded().AssertSqlError("execute as user = 'u'; insert dbo.col_t (id) values (2)", 229);
        Contains("INSERT permission was denied on the object 'col_t'", ex.Message);
    }

    [TestMethod]
    public void DatabasePermissions_SurfaceMinorId_AndColName()
    {
        var sim = Seeded();
        using var reader = sim.ExecuteReader(
            "select p.minor_id, col_name(p.major_id, p.minor_id), p.state_desc " +
            "from sys.database_permissions p " +
            "where p.major_id = object_id('dbo.col_t') and p.permission_name = 'SELECT' " +
            "order by p.minor_id");
        // GRANT id (1), GRANT a (2), DENY c (4).
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("id", reader.GetString(1));
        AreEqual("GRANT", reader.GetString(2));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual("a", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(4, reader.GetInt32(0));
        AreEqual("c", reader.GetString(1));
        AreEqual("DENY", reader.GetString(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void GrantColumnList_UnknownColumn_Raises4615()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int)",
            "create user u without login");
        var ex = sim.AssertSqlError("grant select (nope) on dbo.t to u", 4615);
        Contains("Invalid column name 'nope'", ex.Message);
    }

    [TestMethod]
    public void ColumnGrant_RevealsAllColumnMetadata()
    {
        // Bundle 2 tie-in: a column grant reveals the object object-grain, so
        // sys.columns shows every column (incl. the ungranted / denied ones).
        var sim = Seeded();
        AreEqual(4, sim.ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.columns where object_id = object_id('dbo.col_t')"));
    }

    // ---- Column list after the object name: GRANT SELECT ON t (cols) ----
    // The alternate placement real SQL Server also accepts; it applies the
    // columns to every permission in the statement.

    [TestMethod]
    public void ColumnListAfterObjectName_Enforces_LikePermissionPlacement()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int, secret int)",
            "insert dbo.t values (7, 8)",
            "create user u without login",
            "grant select on dbo.t (id) to u");
        AreEqual(7, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t"));
        var ex = sim.AssertSqlError("execute as user = 'u'; select secret from dbo.t", 230);
        Contains("column 'secret'", ex.Message);
    }

    [TestMethod]
    public void ColumnListAfterObjectName_StoredAsMinorIdRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int, secret int)",
            "create user u without login",
            "grant select on dbo.t (id) to u");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.database_permissions " +
            "where major_id = object_id('dbo.t') and minor_id = 1 and permission_name = 'SELECT'"));
    }

    [TestMethod]
    public void ColumnList_BothPlacements_Raises1019()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int, b int)",
            "create user u without login");
        var ex = sim.AssertSqlError("grant select (a) on dbo.t (b) to u", 1019);
        Contains("Invalid column list after object name", ex.Message);
    }

    [TestMethod]
    public void ColumnList_OnSchemaScope_Raises1020()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u without login");
        var ex = sim.AssertSqlError("grant select on schema::dbo (a) to u", 1020);
        Contains("Sub-entity lists", ex.Message);
    }
}
