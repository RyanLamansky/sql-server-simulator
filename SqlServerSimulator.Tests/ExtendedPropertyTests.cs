using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the extended-properties surface — <c>sp_addextendedproperty</c>,
/// <c>sp_updateextendedproperty</c>, <c>sp_dropextendedproperty</c>,
/// <c>sys.extended_properties</c>, and <c>fn_listextendedproperty</c>. Third
/// bacpac prerequisite (after database-options expansion + UDDTs);
/// AdventureWorks2025 carries 538 <c>SqlExtendedProperty</c> elements that
/// the loader translates to <c>EXEC sp_addextendedproperty</c> calls.
/// Behavior probed against SQL Server 2025 (2026-05-14).
/// </summary>
[TestClass]
public class ExtendedPropertyTests
{
    [TestMethod]
    public void AddOnSchema_ThenReadFromSysExtendedProperties()
        => AreEqual("dbo schema notes", new Simulation().ExecuteScalar("""
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'dbo schema notes',
                @level0type=N'SCHEMA', @level0name=N'dbo';
            SELECT CAST(value AS nvarchar(MAX)) FROM sys.extended_properties WHERE name = 'MS_Description' AND class = 3
            """));

    [TestMethod]
    public void AddOnTable_RowShapeMatchesProbe()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE dbo.t1 (id int);
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'table notes',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1';
            """);
        AreEqual((byte)1, sim.ExecuteScalar(
            "SELECT class FROM sys.extended_properties WHERE name = 'MS_Description'"));
        AreEqual("OBJECT_OR_COLUMN", sim.ExecuteScalar(
            "SELECT class_desc FROM sys.extended_properties WHERE name = 'MS_Description'"));
        AreEqual(0, sim.ExecuteScalar(
            "SELECT minor_id FROM sys.extended_properties WHERE name = 'MS_Description'"));
    }

    [TestMethod]
    public void AddOnColumn_MinorIdIsColumnOrdinal()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE dbo.t1 (id int, name nvarchar(50));
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'col notes',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1',
                @level2type=N'COLUMN', @level2name=N'name';
            """);
        // `name` is the 2nd column, minor_id = 2 (1-based ordinal per probe).
        AreEqual(2, sim.ExecuteScalar(
            "SELECT minor_id FROM sys.extended_properties WHERE name = 'MS_Description'"));
    }

    [TestMethod]
    public void DatabaseLevel_ClassIs0()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "EXEC sp_addextendedproperty @name=N'DBDesc', @value=N'whole-db notes'");
        AreEqual((byte)0, sim.ExecuteScalar(
            "SELECT class FROM sys.extended_properties WHERE name = 'DBDesc'"));
        AreEqual("DATABASE", sim.ExecuteScalar(
            "SELECT class_desc FROM sys.extended_properties WHERE name = 'DBDesc'"));
    }

    [TestMethod]
    public void DuplicateAdd_Schema_RaisesMsg15233()
    {
        var ex = new Simulation().AssertSqlError("""
            EXEC sp_addextendedproperty @name=N'X', @value=N'v',
                @level0type=N'SCHEMA', @level0name=N'dbo';
            EXEC sp_addextendedproperty @name=N'X', @value=N'v',
                @level0type=N'SCHEMA', @level0name=N'dbo';
            """, 15233);
        Contains("'X'", ex.Message);
        Contains("'dbo'", ex.Message);
    }

    [TestMethod]
    public void DuplicateAdd_Database_TargetLabelIsObjectSpecified()
    {
        // Probe-confirmed: DB-level dup uses the literal `'object specified'`
        // token in the Msg 15233 wording.
        var ex = new Simulation().AssertSqlError("""
            EXEC sp_addextendedproperty @name=N'X', @value=N'v';
            EXEC sp_addextendedproperty @name=N'X', @value=N'v';
            """, 15233);
        Contains("'object specified'", ex.Message);
    }

    [TestMethod]
    public void DuplicateAdd_Table_TargetLabelIsSchemaDotTable()
    {
        var ex = new Simulation().AssertSqlError("""
            CREATE TABLE dbo.t1 (id int);
            EXEC sp_addextendedproperty @name=N'X', @value=N'v',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1';
            EXEC sp_addextendedproperty @name=N'X', @value=N'v',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1';
            """, 15233);
        Contains("'dbo.t1'", ex.Message);
    }

    [TestMethod]
    public void DuplicateAdd_Column_TargetLabelIsSchemaDotTableDotColumn()
    {
        var ex = new Simulation().AssertSqlError("""
            CREATE TABLE dbo.t1 (id int);
            EXEC sp_addextendedproperty @name=N'X', @value=N'v',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1',
                @level2type=N'COLUMN', @level2name=N'id';
            EXEC sp_addextendedproperty @name=N'X', @value=N'v',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1',
                @level2type=N'COLUMN', @level2name=N'id';
            """, 15233);
        Contains("'dbo.t1.id'", ex.Message);
    }

    [TestMethod]
    public void Update_ChangesValue()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            EXEC sp_addextendedproperty @name=N'X', @value=N'old',
                @level0type=N'SCHEMA', @level0name=N'dbo';
            EXEC sp_updateextendedproperty @name=N'X', @value=N'new',
                @level0type=N'SCHEMA', @level0name=N'dbo';
            """);
        AreEqual("new", sim.ExecuteScalar(
            "SELECT CAST(value AS nvarchar(MAX)) FROM sys.extended_properties WHERE name = 'X'"));
    }

    [TestMethod]
    public void Update_OnMissing_RaisesMsg15217()
    {
        var ex = new Simulation().AssertSqlError(
            "EXEC sp_updateextendedproperty @name=N'NoSuch', @value=N'x', @level0type=N'SCHEMA', @level0name=N'dbo'",
            15217);
        Contains("'NoSuch'", ex.Message);
    }

    [TestMethod]
    public void Drop_RemovesEntry()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            EXEC sp_addextendedproperty @name=N'X', @value=N'v', @level0type=N'SCHEMA', @level0name=N'dbo';
            EXEC sp_dropextendedproperty @name=N'X', @level0type=N'SCHEMA', @level0name=N'dbo';
            """);
        AreEqual(0, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.extended_properties WHERE name = 'X'"));
    }

    [TestMethod]
    public void Drop_OnMissing_RaisesMsg15217()
        => new Simulation().AssertSqlError(
            "EXEC sp_dropextendedproperty @name=N'NoSuch', @level0type=N'SCHEMA', @level0name=N'dbo'",
            15217);

    [TestMethod]
    public void BadSchemaName_RaisesMsg15135()
    {
        var ex = new Simulation().AssertSqlError(
            "EXEC sp_addextendedproperty @name=N'X', @value=N'x', @level0type=N'SCHEMA', @level0name=N'no_such_schema'",
            15135);
        Contains("'no_such_schema'", ex.Message);
    }

    [TestMethod]
    public void BadTableName_RaisesMsg15135()
    {
        var ex = new Simulation().AssertSqlError(
            "EXEC sp_addextendedproperty @name=N'X', @value=N'x', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'no_such_table'",
            15135);
        Contains("'dbo.no_such_table'", ex.Message);
    }

    [TestMethod]
    public void BadColumnName_RaisesMsg15135()
    {
        var ex = new Simulation().AssertSqlError("""
            CREATE TABLE dbo.t1 (id int);
            EXEC sp_addextendedproperty @name=N'X', @value=N'x',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1',
                @level2type=N'COLUMN', @level2name=N'no_such_col';
            """, 15135);
        Contains("'dbo.t1.no_such_col'", ex.Message);
    }

    [TestMethod]
    public void BadLevel0Type_RaisesMsg15600()
        => new Simulation().AssertSqlError(
            "EXEC sp_addextendedproperty @name=N'X', @value=N'x', @level0type=N'BOGUS', @level0name=N'x'",
            15600);

    [TestMethod]
    public void FnListExtendedProperty_TableLevel_ReturnsOneRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE dbo.t1 (id int);
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'table notes',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1';
            """);
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand(
            "SELECT objtype, objname, name FROM fn_listextendedproperty(NULL, 'SCHEMA', 'dbo', 'TABLE', 't1', NULL, NULL)");
        using var r = cmd.ExecuteReader();
        IsTrue(r.Read());
        AreEqual("TABLE", r.GetString(0));
        AreEqual("t1", r.GetString(1));
        AreEqual("MS_Description", r.GetString(2));
        IsFalse(r.Read());
    }

    [TestMethod]
    public void FnListExtendedProperty_NameFilter_AppliesAcrossLevels()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE dbo.t1 (id int);
            EXEC sp_addextendedproperty @name=N'Author', @value=N'alice',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1';
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'desc',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1';
            """);
        AreEqual(1, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM fn_listextendedproperty('Author', 'SCHEMA', 'dbo', 'TABLE', 't1', NULL, NULL)"));
        AreEqual(2, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM fn_listextendedproperty(NULL, 'SCHEMA', 'dbo', 'TABLE', 't1', NULL, NULL)"));
    }

    [TestMethod]
    public void FnListExtendedProperty_ColumnLevel_FindsByOrdinal()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE dbo.t1 (id int, name nvarchar(50));
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'name col notes',
                @level0type=N'SCHEMA', @level0name=N'dbo',
                @level1type=N'TABLE', @level1name=N't1',
                @level2type=N'COLUMN', @level2name=N'name';
            """);
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand(
            "SELECT objtype, objname FROM fn_listextendedproperty(NULL, 'SCHEMA', 'dbo', 'TABLE', 't1', 'COLUMN', 'name')");
        using var r = cmd.ExecuteReader();
        IsTrue(r.Read());
        AreEqual("COLUMN", r.GetString(0));
        AreEqual("name", r.GetString(1));
    }

    /// <summary>
    /// Probe-confirmed: fn_listextendedproperty returns zero rows on a
    /// missing object (NOT Msg 15135 — that's the sproc path).
    /// </summary>
    [TestMethod]
    public void FnListExtendedProperty_MissingTarget_ReturnsZeroRows()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "SELECT COUNT(*) FROM fn_listextendedproperty(NULL, 'SCHEMA', 'dbo', 'TABLE', 'no_such_table', NULL, NULL)"));

    [TestMethod]
    public void FnListExtendedProperty_DefaultWildcard_FansOutAcrossSchemaTables()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE dbo.t1 (id int);
            CREATE TABLE dbo.t2 (id int);
            EXEC sp_addextendedproperty @name=N'Desc', @value=N't1', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N't1';
            EXEC sp_addextendedproperty @name=N'Desc', @value=N't2', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N't2';
            """);
        // 'default' wildcard at level1name expands to every table in the schema.
        AreEqual(2, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM fn_listextendedproperty(NULL, 'SCHEMA', 'dbo', 'TABLE', 'default', NULL, NULL)"));
    }
}
