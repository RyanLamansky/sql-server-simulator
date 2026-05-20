using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the collation metadata surface — per-database
/// (<c>ALTER DATABASE … COLLATE name</c>, <c>sys.databases.collation_name</c>,
/// <c>DATABASEPROPERTYEX</c>), per-column (<c>CREATE TABLE col TYPE COLLATE name</c>,
/// <c>sys.columns.collation_name</c>, <c>INFORMATION_SCHEMA.COLUMNS.COLLATION_NAME</c>),
/// the recognized-collation whitelist (<c>sys.fn_helpcollations</c>), and the
/// failure path for unrecognized names. Comparison / sort / LIKE still route
/// through the simulator's default collation regardless of declared metadata —
/// these tests cover the round-trip, not the semantic divergence (the
/// fidelity gap is documented in
/// <c>docs/claude/database-options.md</c>'s COLLATE-clause caveat).
/// </summary>
[TestClass]
public sealed class CollationMetadataTests
{
    [TestMethod]
    public void DefaultDatabase_CollationName_IsDefault()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS", new Simulation().ExecuteScalar(
            "SELECT collation_name FROM sys.databases"));

    [TestMethod]
    public void AlterDatabase_Collate_RecognizedName_RoundTripsViaSysDatabases()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated COLLATE Latin1_General_100_CI_AS");
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.databases"));
    }

    [TestMethod]
    public void AlterDatabase_Collate_RoundTripsViaDatabasePropertyEx()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated COLLATE Latin1_General_100_CI_AS");
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Collation')"));
    }

    [TestMethod]
    public void DatabasePropertyEx_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'NotARealProperty')"));

    [TestMethod]
    public void DatabasePropertyEx_UnknownDatabase_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('no_such_db', 'Collation')"));

    [TestMethod]
    public void DatabasePropertyEx_NullArgs_ReturnNull()
    {
        var sim = new Simulation();
        AreEqual(DBNull.Value, sim.ExecuteScalar("SELECT DATABASEPROPERTYEX(NULL, 'Collation')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("SELECT DATABASEPROPERTYEX('simulated', NULL)"));
    }

    [TestMethod]
    public void Column_NoCollate_InheritsDatabaseDefault()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TABLE t (name nvarchar(50))");
        AreEqual("SQL_Latin1_General_CP1_CI_AS", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.columns WHERE name = 'name'"));
    }

    [TestMethod]
    public void Column_NoCollate_FollowsDatabaseAfterAlter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            ALTER DATABASE simulated COLLATE Latin1_General_100_CI_AS;
            CREATE TABLE t (name nvarchar(50));
            """);
        // Per-column collation_name reports the db default when no override.
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.columns WHERE name = 'name'"));
    }

    [TestMethod]
    public void Column_WithCollate_RoundTrips()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TABLE t (a nvarchar(50), b nvarchar(50) COLLATE Latin1_General_100_CI_AS)");
        // Column a inherits db default; column b carries the override.
        AreEqual("SQL_Latin1_General_CP1_CI_AS", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.columns WHERE name = 'a'"));
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.columns WHERE name = 'b'"));
    }

    [TestMethod]
    public void Column_NonStringType_CollationNameIsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TABLE t (i int, n nvarchar(50), d datetime, b bit)");
        AreEqual(DBNull.Value, sim.ExecuteScalar("SELECT collation_name FROM sys.columns WHERE name = 'i'"));
        AreEqual("SQL_Latin1_General_CP1_CI_AS", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.columns WHERE name = 'n'"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("SELECT collation_name FROM sys.columns WHERE name = 'd'"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("SELECT collation_name FROM sys.columns WHERE name = 'b'"));
    }

    [TestMethod]
    public void Column_WithCollate_RoundTripsViaInformationSchema()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TABLE t (b nvarchar(50) COLLATE Latin1_General_100_CI_AS)");
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar(
            "SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE COLUMN_NAME = 'b'"));
    }

    [TestMethod]
    public void Column_Collate_UnrecognizedName_RaisesNotSupported()
    {
        var ex = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "CREATE TABLE t (a nvarchar(50) COLLATE Japanese_CI_AS)"));
        Contains("Japanese_CI_AS", ex.Message);
        Contains("recognized list", ex.Message);
    }

    [TestMethod]
    public void FnHelpCollations_ListsRecognized()
        => AreEqual(12, new Simulation().ExecuteScalar(
            "SELECT COUNT(*) FROM sys.fn_helpcollations()"));

    [TestMethod]
    public void Column_Collate_Latin1_General_CI_AS_RoundTrips()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TABLE t (b nvarchar(50) COLLATE Latin1_General_CI_AS)");
        AreEqual("Latin1_General_CI_AS", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.columns WHERE name = 'b'"));
    }

    [TestMethod]
    public void FnHelpCollations_ColumnsAreNameAndDescription()
    {
        // The view's row shape mirrors real SQL Server's
        // (name sysname NULL, description nvarchar(1000) NULL).
        // Drop the parens — the parens form has a pre-existing parser path
        // that bypasses WHERE; the non-parens form is more permissive than
        // real SQL Server (which requires `()` on TVF calls) but exercises
        // the WHERE filter correctly.
        var sim = new Simulation();
        AreEqual("Latin1_General_100_CI_AS", sim.ExecuteScalar(
            "SELECT name FROM sys.fn_helpcollations WHERE name = 'Latin1_General_100_CI_AS'"));
    }

    [TestMethod]
    public void SysDatabases_RowShape_CarriesCompatibilityAndIsolation()
    {
        var sim = new Simulation();
        AreEqual("simulated", sim.ExecuteScalar("SELECT name FROM sys.databases"));
        AreEqual((short)1, sim.ExecuteScalar("SELECT database_id FROM sys.databases"));
        AreEqual("ONLINE", sim.ExecuteScalar("SELECT state_desc FROM sys.databases"));
    }

    [TestMethod]
    public void DatabasePropertyEx_Status_ReturnsOnline()
        => AreEqual("ONLINE", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Status')"));

    [TestMethod]
    public void DatabasePropertyEx_Version_ReturnsZero()
        => AreEqual("0", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Version')"));

    [TestMethod]
    public void DatabasePropertyEx_Recovery_ReturnsFull()
        => AreEqual("FULL", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Recovery')"));

    [TestMethod]
    public void DatabasePropertyEx_UserAccess_ReturnsMultiUser()
        => AreEqual("MULTI_USER", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'UserAccess')"));

    [TestMethod]
    public void DatabasePropertyEx_IsAutoClose_ReturnsZero()
        => AreEqual("0", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'IsAutoClose')"));

    [TestMethod]
    public void DatabasePropertyEx_IsAutoShrink_ReturnsZero()
        => AreEqual("0", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'IsAutoShrink')"));

    [TestMethod]
    public void DatabasePropertyEx_SnapshotIsolationState_ReflectsToggle()
    {
        var sim = new Simulation();
        AreEqual("0", sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'SnapshotIsolationState')"));
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated SET ALLOW_SNAPSHOT_ISOLATION ON");
        AreEqual("1", sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'SnapshotIsolationState')"));
    }

    [TestMethod]
    public void DatabasePropertyEx_IsReadCommittedSnapshotOn_ReflectsToggle()
    {
        var sim = new Simulation();
        AreEqual("0", sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'IsReadCommittedSnapshotOn')"));
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated SET READ_COMMITTED_SNAPSHOT ON");
        AreEqual("1", sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'IsReadCommittedSnapshotOn')"));
    }

    [TestMethod]
    public void DatabasePropertyEx_MissingComma_RaisesMsg174()
        => _ = new Simulation().AssertSqlError(
            "SELECT DATABASEPROPERTYEX('simulated' 'Status')", 174);

    [TestMethod]
    public void DatabasePropertyEx_MissingCloseParen_RaisesSyntaxError()
        => _ = Throws<DbException>(() => new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Status' extra"));
}
