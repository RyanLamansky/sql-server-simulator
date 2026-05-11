using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>sys.&lt;view&gt;</c> catalog views — <c>sys.schemas</c>,
/// <c>sys.tables</c>, <c>sys.objects</c> — plus the <c>SCHEMA_ID()</c>
/// scalar. Schemas pre-populate with conventional ids (dbo=1,
/// INFORMATION_SCHEMA=3, sys=4); user schemas start at 5. Rows project
/// from live metadata, so changes earlier in the same batch are visible
/// immediately. Probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class CatalogViewTests
{
    [TestMethod]
    public void SchemaId_NoArg_ReturnsDbo()
        => AreEqual(1, new Simulation().ExecuteScalar("select schema_id()"));

    [TestMethod]
    public void SchemaId_Dbo_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select schema_id('dbo')"));

    [TestMethod]
    public void SchemaId_InformationSchema_Returns3()
        => AreEqual(3, new Simulation().ExecuteScalar("select schema_id('INFORMATION_SCHEMA')"));

    [TestMethod]
    public void SchemaId_Sys_Returns4()
        => AreEqual(4, new Simulation().ExecuteScalar("select schema_id('sys')"));

    [TestMethod]
    public void SchemaId_FirstUserSchema_Returns5()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create schema audit;
            select schema_id('audit')
            """));

    [TestMethod]
    public void SchemaId_TwoUserSchemas_5And6()
        => AreEqual(11, new Simulation().ExecuteScalar("""
            create schema audit;
            create schema staging;
            select schema_id('audit') + schema_id('staging')
            """));

    [TestMethod]
    public void SchemaId_Missing_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select schema_id('nope')");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void SchemaId_NullArg_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select schema_id(null)");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void SysSchemas_ListsBuiltInSchemas()
    {
        using var reader = new Simulation().ExecuteReader(
            "select schema_id, name from sys.schemas order by schema_id");
        var rows = new List<(int Id, string Name)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        CollectionAssert.AreEquivalent(
            new[] { (1, "dbo"), (3, "INFORMATION_SCHEMA"), (4, "sys") },
            rows);
    }

    [TestMethod]
    public void SysSchemas_IncludesUserSchemas()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create schema audit;
            select schema_id from sys.schemas where name = 'audit'
            """));

    [TestMethod]
    public void SysSchemas_PrincipalIdIsNull()
    {
        using var reader = new Simulation().ExecuteReader(
            "select principal_id from sys.schemas where name = 'dbo'");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void SysTables_EmptyByDefault()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from sys.tables"));

    [TestMethod]
    public void SysTables_AfterCreateTable_HasRow()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table foo (id int);
            select count(*) from sys.tables where name = 'foo'
            """));

    [TestMethod]
    public void SysTables_ProjectsObjectIdMatchingObjectIdFunction()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select t.object_id, object_id('foo') as fn from sys.tables t where t.name = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void SysTables_SchemaIdMatchesSchemaIdFunction()
    {
        using var reader = new Simulation().ExecuteReader("""
            create schema audit;
            create table audit.bar (id int);
            select t.schema_id, schema_id('audit') as fn from sys.tables t where t.name = 'bar'
            """);
        IsTrue(reader.Read());
        AreEqual(reader.GetInt32(0), reader.GetInt32(1));
        AreEqual(5, reader.GetInt32(0));
    }

    [TestMethod]
    public void SysTables_TypeIsCharTwoWithTrailingSpace()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select type, datalength(type) as dl, type_desc from sys.tables where name = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual("U ", reader.GetString(0));
        AreEqual(2, reader.GetInt32(1));
        AreEqual("USER_TABLE", reader.GetString(2));
    }

    [TestMethod]
    public void SysTables_IsMsShippedFalse()
        => IsFalse((bool)new Simulation().ExecuteScalar("""
            create table foo (id int);
            select is_ms_shipped from sys.tables where name = 'foo'
            """)!);

    [TestMethod]
    public void SysTables_CreateDateIsRecentUtc()
    {
        // Catalog view returns the captured CreateDate as datetime. Verify
        // it lands within a small window of "now" — exact equality isn't
        // possible (test setup latency), but membership of a ±5min window
        // proves the per-statement freeze surfaced correctly.
        var now = DateTime.UtcNow;
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select create_date from sys.tables where name = 'foo'
            """);
        IsTrue(reader.Read());
        var createDate = reader.GetDateTime(0);
        IsLessThan(TimeSpan.FromMinutes(5), (now - createDate).Duration());
    }

    [TestMethod]
    public void SysTables_DropTableRemovesRow()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table foo (id int);
            drop table foo;
            select count(*) from sys.tables where name = 'foo'
            """));

    [TestMethod]
    public void SysObjects_IncludesUserTableAndPK()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int primary key);
            select type, type_desc, parent_object_id from sys.objects where schema_id = 1 order by type
            """);
        var rows = new List<(string Type, string Desc, int Parent)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        HasCount(2, rows);
        AreEqual("PK", rows[0].Type);
        AreEqual("PRIMARY_KEY_CONSTRAINT", rows[0].Desc);
        AreNotEqual(0, rows[0].Parent);
        AreEqual("U ", rows[1].Type);
        AreEqual("USER_TABLE", rows[1].Desc);
        AreEqual(0, rows[1].Parent);
    }

    [TestMethod]
    public void SysObjects_UniqueConstraintGetsUqRow()
        => AreEqual("UNIQUE_CONSTRAINT", new Simulation().ExecuteScalar("""
            create table foo (id int, code nvarchar(10) unique);
            select type_desc from sys.objects where type = 'UQ'
            """));

    [TestMethod]
    public void SysObjects_CheckConstraintGetsCRow()
        => AreEqual("CHECK_CONSTRAINT", new Simulation().ExecuteScalar("""
            create table foo (id int check (id > 0));
            select type_desc from sys.objects where type = 'C '
            """));

    [TestMethod]
    public void SysObjects_PkParentLinksBackToTable()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int primary key);
            select parent_object_id from sys.objects where type = 'PK'
            """);
        IsTrue(reader.Read());
        var parent = reader.GetInt32(0);
        AreEqual(parent, new Simulation().ExecuteScalar("""
            create table foo (id int primary key);
            select object_id('foo')
            """));
    }

    [TestMethod]
    public void SysObjects_JoinSysTablesWorks()
    {
        // Common app idiom — join sys.objects with sys.tables (or filter by
        // object_id from each). Verify the catalog views interoperate.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            create table bar (id int);
            select o.name from sys.objects o inner join sys.tables t on o.object_id = t.object_id order by o.name
            """);
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "bar", "foo" }, names);
    }

    [TestMethod]
    public void SysSchemas_QualifiedFromCurrentDatabase()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select schema_id from simulated.sys.schemas where name = 'dbo'"));

    [TestMethod]
    public void SysTables_WrongDatabaseQualifier_Msg208()
        => new Simulation().AssertSqlError(
            "select count(*) from baddb.sys.tables",
            208);

    /// <summary>
    /// Real SQL Server requires sys.-qualification — bare `tables` hits
    /// the regular user-table-not-found path.
    /// </summary>
    [TestMethod]
    public void UnqualifiedTables_Msg208()
        => new Simulation().AssertSqlError("select * from tables", 208);

    [TestMethod]
    public void SysTables_UnknownView_Msg208()
        => new Simulation().AssertSqlError(
            "select * from sys.this_is_not_a_real_view",
            208);

    [TestMethod]
    public void SysTables_WithAlias_Works()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table foo (id int);
            select count(*) from sys.tables t where t.name = 'foo'
            """));

    [TestMethod]
    public void SysTables_OrdersTablesByObjectId()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table a (id int);
            create table b (id int);
            create table c (id int);
            select name from sys.tables order by object_id
            """);
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, names);
    }

    [TestMethod]
    public void CreateTable_CannotAddToSysSchema()
        => Throws<NotSupportedException>(() =>
            new Simulation().ExecuteNonQuery("create table sys.foo (id int)"));

    [TestMethod]
    public void CreateTable_CannotAddToInformationSchema()
        => Throws<NotSupportedException>(() =>
            new Simulation().ExecuteNonQuery("create table INFORMATION_SCHEMA.foo (id int)"));

    [TestMethod]
    public void SysSchemas_DropSchemaNotModeled()
    {
        // Sanity: DROP SCHEMA isn't modeled (existing limitation). Schemas
        // can only be added; sys / dbo / INFORMATION_SCHEMA are always there.
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from sys.schemas";
        AreEqual(3, cmd.ExecuteScalar());
    }
}
