using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Exercises scalar user-defined alias types (UDDTs) created via
/// <c>CREATE TYPE schema.name FROM &lt;builtin&gt;[(N[, S])] [NULL | NOT NULL]</c>.
/// Alias types are the second bacpac prerequisite (after the database-options
/// expansion); AdventureWorks2025 declares 6 of them (<c>AccountNumber</c>,
/// <c>Flag</c>, <c>Name</c>, <c>NameStyle</c>, <c>OrderNumber</c>,
/// <c>Phone</c>). Behavior probed against SQL Server 2025 (2026-05-14).
/// </summary>
[TestClass]
public class AliasTypeTests
{
    [TestMethod]
    public void CreateAlias_NotNull_Then_ColumnInheritsNotNullByDefault()
    {
        // Probe-confirmed: column with no explicit nullability marker inherits
        // NOT NULL from the alias.
        var ex = new Simulation().AssertSqlError("""
            CREATE TYPE dbo.AccountNumber FROM nvarchar(15) NOT NULL;
            CREATE TABLE t (c dbo.AccountNumber);
            INSERT INTO t (c) VALUES (NULL);
            """, 515);
        Contains("does not allow nulls", ex.Message);
    }

    /// <summary>
    /// Bare CREATE TYPE (no NULL/NOT NULL marker) → alias is nullable; the
    /// column declaration without its own marker stays nullable.
    /// </summary>
    [TestMethod]
    public void CreateAlias_Bare_ColumnIsNullable()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            CREATE TYPE dbo.Probe FROM int;
            CREATE TABLE t (c dbo.Probe);
            INSERT INTO t (c) VALUES (NULL);
            SELECT COUNT(*) FROM t WHERE c IS NULL
            """));

    [TestMethod]
    public void CreateAlias_ExplicitNullKeyword_AliasIsNullable()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            CREATE TYPE dbo.Probe FROM int NULL;
            CREATE TABLE t (c dbo.Probe);
            INSERT INTO t (c) VALUES (NULL);
            SELECT COUNT(*) FROM t WHERE c IS NULL
            """));

    /// <summary>
    /// Column-level explicit NULL overrides alias-defined NOT NULL — probe-
    /// confirmed (real SQL Server treats the column-side marker as
    /// authoritative when present).
    /// </summary>
    [TestMethod]
    public void ColumnNullOverride_TrumpsAliasNotNull()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            CREATE TYPE dbo.Tight FROM int NOT NULL;
            CREATE TABLE t (c dbo.Tight NULL);
            INSERT INTO t (c) VALUES (NULL);
            SELECT COUNT(*) FROM t WHERE c IS NULL
            """));

    /// <summary>
    /// Probe-confirmed: alias can be referenced without the schema qualifier
    /// when it's in dbo (the default schema).
    /// </summary>
    [TestMethod]
    public void UnqualifiedReference_Works()
        => AreEqual(42, new Simulation().ExecuteScalar("""
            CREATE TYPE dbo.Probe FROM int;
            CREATE TABLE t (c Probe);
            INSERT INTO t (c) VALUES (42);
            SELECT c FROM t
            """));

    [TestMethod]
    public void QualifiedReference_Works()
        => AreEqual(42, new Simulation().ExecuteScalar("""
            CREATE TYPE dbo.Probe FROM int;
            CREATE TABLE t (c [dbo].[Probe]);
            INSERT INTO t (c) VALUES (42);
            SELECT c FROM t
            """));

    [TestMethod]
    public void LengthAtUsageSite_RaisesMsg2716()
    {
        // Probe-confirmed verbatim: Msg 2716 St 3 with the alias's fully-
        // qualified name in the message.
        var ex = new Simulation().AssertSqlError("""
            CREATE TYPE dbo.Name FROM nvarchar(50) NOT NULL;
            CREATE TABLE t (c dbo.Name(100));
            """, 2716);
        Contains("dbo.Name", ex.Message);
        Contains("Cannot specify a column width", ex.Message);
    }

    [TestMethod]
    public void DuplicateTypeName_RaisesMsg219()
    {
        var ex = new Simulation().AssertSqlError("""
            CREATE TYPE dbo.Probe FROM int;
            CREATE TYPE dbo.Probe FROM int;
            """, 219);
        Contains("dbo.Probe", ex.Message);
    }

    [TestMethod]
    public void AliasName_CollidesWithTableType_RaisesMsg219()
    {
        // Alias + table types share one type-name namespace.
        var ex = new Simulation().AssertSqlError("""
            CREATE TYPE dbo.Shared AS TABLE (id int);
            CREATE TYPE dbo.Shared FROM int;
            """, 219);
        Contains("dbo.Shared", ex.Message);
    }

    [TestMethod]
    public void InvalidBaseType_RaisesMsg222()
    {
        var ex = new Simulation().AssertSqlError(
            "CREATE TYPE dbo.Bogus FROM not_a_type",
            222);
        Contains("not_a_type", ex.Message);
        Contains("not a valid base type", ex.Message);
    }

    [TestMethod]
    public void DropType_OnAlias_Removes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TYPE dbo.Probe FROM int");
        AreEqual(1, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.types WHERE name = 'Probe' AND is_user_defined = 1"));
        _ = sim.ExecuteNonQuery("DROP TYPE dbo.Probe");
        AreEqual(0, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.types WHERE name = 'Probe' AND is_user_defined = 1"));
    }

    [TestMethod]
    public void DropType_OnMissingAlias_RaisesMsg218()
    {
        _ = new Simulation().AssertSqlError("DROP TYPE dbo.NoSuchAlias", 218);
    }

    [TestMethod]
    public void DropType_IfExists_OnMissingAlias_Succeeds()
        => AreEqual(-1, new Simulation().ExecuteNonQuery("DROP TYPE IF EXISTS dbo.NoSuchAlias"));

    [TestMethod]
    public void SysTypes_Row_ShipsCorrectShape()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TYPE dbo.AccountNumber FROM nvarchar(15) NOT NULL");
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand(
            "SELECT system_type_id, is_user_defined, is_table_type, is_nullable FROM sys.types WHERE name = 'AccountNumber'");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        // nvarchar's system_type_id is 231 per probe.
        AreEqual((byte)231, reader.GetByte(0));
        IsTrue(reader.GetBoolean(1));   // is_user_defined
        IsFalse(reader.GetBoolean(2));  // is_table_type
        IsFalse(reader.GetBoolean(3));  // is_nullable (alias was NOT NULL)
    }

    [TestMethod]
    public void SysTypes_BareAlias_IsNullable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TYPE dbo.Probe FROM int");
        IsTrue((bool)sim.ExecuteScalar(
            "SELECT is_nullable FROM sys.types WHERE name = 'Probe'")!);
    }

    [TestMethod]
    public void Declare_AliasTypedVariable_Works()
        => AreEqual(42, new Simulation().ExecuteScalar("""
            CREATE TYPE dbo.Probe FROM int;
            DECLARE @v dbo.Probe;
            SET @v = 42;
            SELECT @v
            """));

    [TestMethod]
    public void AdventureWorksAliasTypes_LoadSuccessfully()
    {
        // Smoke test for the AW alias-type set — all six should be declarable
        // and usable as column types end-to-end.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE TYPE dbo.AccountNumber FROM nvarchar(15) NOT NULL;
            CREATE TYPE dbo.Flag FROM bit NOT NULL;
            CREATE TYPE dbo.Name FROM nvarchar(50) NOT NULL;
            CREATE TYPE dbo.NameStyle FROM bit NOT NULL;
            CREATE TYPE dbo.OrderNumber FROM nvarchar(25) NOT NULL;
            CREATE TYPE dbo.Phone FROM nvarchar(25);
            CREATE TABLE dbo.Customer (
                AccountNumber [dbo].[AccountNumber],
                Title [dbo].[Name],
                NameStyle [dbo].[NameStyle],
                Phone [dbo].[Phone]);
            INSERT INTO dbo.Customer (AccountNumber, Title, NameStyle, Phone)
            VALUES ('AW-001', 'Ms.', 0, '555-1234');
            """);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM dbo.Customer"));
        // Verify NOT NULL inheritance from alias: AccountNumber, Title,
        // NameStyle should all be NOT NULL; Phone is nullable.
        AreEqual(3, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customer') AND is_nullable = 0"));
    }

    [TestMethod]
    public void CrossSchema_Alias_Resolves()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            CREATE SCHEMA HR;
            """);
        _ = sim.ExecuteNonQuery("""
            CREATE TYPE HR.EmployeeId FROM int NOT NULL;
            CREATE TABLE HR.Employee (Id HR.EmployeeId);
            INSERT INTO HR.Employee (Id) VALUES (1);
            """);
        AreEqual(1, sim.ExecuteScalar("SELECT Id FROM HR.Employee"));
    }
}
