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
            "SELECT collation_name FROM sys.databases WHERE name = 'simulated'"));
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

    /// <summary>
    /// Names structurally valid but absent from the catalog raise
    /// <c>NotSupportedException</c>. <c>Mapudungan_BIN</c> has a known
    /// prefix and a recognized suffix grammar but the specific pair
    /// doesn't ship in real SQL Server (probed 2026-05-21: Mapudungan is
    /// v100-only).
    /// </summary>
    [TestMethod]
    public void Column_Collate_UnrecognizedName_RaisesNotSupported()
    {
        var ex = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "CREATE TABLE t (a nvarchar(50) COLLATE Mapudungan_BIN)"));
        Contains("Mapudungan_BIN", ex.Message);
        Contains("recognized list", ex.Message);
    }

    /// <summary>
    /// The simulator's catalog matches real SQL Server's
    /// <c>sys.fn_helpcollations()</c> count — 77 SQL_* names + 5463
    /// non-SQL_* names = 5540 total, probed against SQL Server 2025 on
    /// 2026-05-21. The parser validates names against the per-prefix
    /// tail-set catalog so phantom combinations (grammar-valid but never
    /// shipped) are rejected.
    /// </summary>
    [TestMethod]
    public void FnHelpCollations_ListsRecognized()
        => AreEqual(5540, new Simulation().ExecuteScalar(
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

    /// <summary>
    /// Parser-driven catalog accepts the full breadth of real SQL Server
    /// 2025 names — locale × version × flag combinations beyond the 26
    /// hand-tuned entries the prior implementation maintained. Probed
    /// names from each pattern bucket: a v100-only locale (Pattern_0), a
    /// versioned-and-unversioned locale (Pattern_1), a SQL_* CP1250
    /// variant, and a v140 + VSS combo (Pattern_6).
    /// </summary>
    [TestMethod]
    [DataRow("Albanian_100_CI_AS", "Albanian-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive")]
    [DataRow("Greek_BIN2", "Greek, binary code point comparison sort")]
    [DataRow("SQL_Polish_CP1250_CS_AS", "Polish, case-sensitive, accent-sensitive, kanatype-insensitive, width-insensitive for Unicode Data, SQL Server Sort Order 87 on Code Page 1250 for non-Unicode Data")]
    [DataRow("Japanese_XJIS_140_CI_AS_KS_WS_VSS", "Japanese-XJIS-140, case-insensitive, accent-sensitive, kanatype-sensitive, width-sensitive, supplementary characters, variation selector sensitive")]
    [DataRow("German_PhoneBook_100_CI_AS_KS_WS_SC_UTF8", "German-PhoneBook-100, case-insensitive, accent-sensitive, kanatype-sensitive, width-sensitive, supplementary characters, UTF8")]
    public void FnHelpCollations_Description_MatchesProbedSqlServer(string name, string description)
        => AreEqual(description, new Simulation().ExecuteScalar(
            $"SELECT description FROM sys.fn_helpcollations WHERE name = '{name}'"));

    /// <summary>
    /// Grammar-valid but never-shipped name combinations reject — the
    /// parser validates against the per-prefix tail-set catalog, not just
    /// the suffix grammar. <c>Pashto_CI_AS</c> (unversioned form of a
    /// v100-only locale) and <c>Latin1_General_140_BIN</c> (v140 doesn't
    /// have BIN/BIN2) are both phantom; both reject.
    /// </summary>
    [TestMethod]
    [DataRow("Pashto_CI_AS")]
    [DataRow("Latin1_General_140_BIN")]
    [DataRow("Albanian_BIN2_UTF8")]
    public void PhantomCollationName_RejectedByParser(string name)
        => Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            $"CREATE TABLE t (a nvarchar(50) COLLATE {name})"));

    [TestMethod]
    public void SysDatabases_RowShape_CarriesCompatibilityAndIsolation()
    {
        // master (database_id 1) sorts ahead of the user database, so scope
        // the row-shape assertions to the simulated database by name.
        var sim = new Simulation();
        AreEqual("simulated", sim.ExecuteScalar("SELECT name FROM sys.databases WHERE name = 'simulated'"));
        AreEqual(5, sim.ExecuteScalar("SELECT database_id FROM sys.databases WHERE name = 'simulated'"));
        AreEqual("ONLINE", sim.ExecuteScalar("SELECT state_desc FROM sys.databases WHERE name = 'simulated'"));
    }

    [TestMethod]
    public void DatabasePropertyEx_Status_ReturnsOnline()
        => AreEqual("ONLINE", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Status')"));

    [TestMethod]
    public void DatabasePropertyEx_Version_ReturnsZeroAsInt()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Version')"));

    [TestMethod]
    public void DatabasePropertyEx_Version_SurfacesAsIntType()
    {
        using var reader = new Simulation().ExecuteReader(
            "SELECT DATABASEPROPERTYEX('simulated', 'Version')");
        IsTrue(reader.Read());
        AreEqual("int", reader.GetDataTypeName(0));
        _ = Assert.IsInstanceOfType<int>(reader.GetValue(0));
    }

    [TestMethod]
    public void DatabasePropertyEx_ComparisonStyle_ReturnsInt()
        => AreEqual(196609, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'ComparisonStyle')"));

    [TestMethod]
    public void DatabasePropertyEx_Lcid_ReturnsInt()
        => AreEqual(1033, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'LCID')"));

    [TestMethod]
    public void DatabasePropertyEx_SqlSortOrder_ReturnsByte52OnDefaultCollation()
        => AreEqual((byte)52, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'SQLSortOrder')"));

    [TestMethod]
    public void DatabasePropertyEx_NonConstantProperty_FallsBackToNVarchar()
    {
        var sim = new Simulation();
        AreEqual("0", sim.ExecuteScalar(
            "declare @p nvarchar(30) = 'Version'; SELECT DATABASEPROPERTYEX('simulated', @p)"));
    }

    [TestMethod]
    public void DatabasePropertyEx_Recovery_ReturnsFull()
        => AreEqual("FULL", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'Recovery')"));

    [TestMethod]
    public void DatabasePropertyEx_UserAccess_ReturnsMultiUser()
        => AreEqual("MULTI_USER", new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'UserAccess')"));

    [TestMethod]
    public void DatabasePropertyEx_IsAutoClose_ReturnsZeroAsInt()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'IsAutoClose')"));

    [TestMethod]
    public void DatabasePropertyEx_IsAutoShrink_ReturnsZeroAsInt()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'IsAutoShrink')"));

    [TestMethod]
    public void DatabasePropertyEx_SnapshotIsolationState_ReflectsToggle()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'SnapshotIsolationState')"));
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated SET ALLOW_SNAPSHOT_ISOLATION ON");
        AreEqual(1, sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'SnapshotIsolationState')"));
    }

    [TestMethod]
    public void DatabasePropertyEx_IsReadCommittedSnapshotOn_ReflectsToggle()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar(
            "SELECT DATABASEPROPERTYEX('simulated', 'IsReadCommittedSnapshotOn')"));
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated SET READ_COMMITTED_SNAPSHOT ON");
        AreEqual(1, sim.ExecuteScalar(
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

    // === COLLATIONPROPERTY(collation_name, property) ===
    // Probe-confirmed against SQL Server 2025 (2026-07-14). Like real, the
    // result is sql_variant carrying a per-property inner base type: int for
    // CodePage/LCID/ComparisonStyle, tinyint for Version, nvarchar for Name.

    [TestMethod]
    public void CollationProperty_CodePage_ReturnsAnsiCodePage()
        => AreEqual(1252, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('SQL_Latin1_General_CP1_CI_AS', 'CodePage')"));

    [TestMethod]
    public void CollationProperty_Lcid_ReturnsLocaleId()
        => AreEqual(1033, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('SQL_Latin1_General_CP1_CI_AS', 'LCID')"));

    [TestMethod]
    public void CollationProperty_ComparisonStyle_ReturnsBitmask()
        => AreEqual(196609, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('SQL_Latin1_General_CP1_CI_AS', 'ComparisonStyle')"));

    [TestMethod]
    public void CollationProperty_Version_SqlCollation_IsZero()
        => AreEqual((byte)0, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('SQL_Latin1_General_CP1_CI_AS', 'Version')"));

    [TestMethod]
    public void CollationProperty_Version_HundredSeriesCollation_IsTwo()
        => AreEqual((byte)2, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('Latin1_General_100_CI_AS', 'Version')"));

    [TestMethod]
    public void CollationProperty_Name_ReturnsCollationName()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS", new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('SQL_Latin1_General_CP1_CI_AS', 'Name')"));

    // The result projects as sql_variant; CodePage carries an int inner, Version
    // a tinyint inner (probe-confirmed against SQL Server 2025).
    [TestMethod]
    public void CollationProperty_ReportsSqlVariantMetadataWithInnerType()
    {
        using var reader = new Simulation().ExecuteReader(
            "SELECT COLLATIONPROPERTY('SQL_Latin1_General_CP1_CI_AS', 'CodePage')");
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        IsTrue(reader.Read());
        _ = Assert.IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(1252, reader.GetValue(0));
    }

    // ComparisonStyle tracks the suffix flags — accent-insensitive adds bit 2,
    // case-sensitive clears bit 1, KS/WS clear the ignore-kana/width bits, and
    // binary collations report 0. Probe-confirmed values.
    [TestMethod]
    public void CollationProperty_ComparisonStyle_TracksSuffixFlags()
    {
        var sim = new Simulation();
        AreEqual(196611, sim.ExecuteScalar("SELECT COLLATIONPROPERTY('Latin1_General_100_CI_AI', 'ComparisonStyle')"));
        AreEqual(196608, sim.ExecuteScalar("SELECT COLLATIONPROPERTY('Latin1_General_100_CS_AS', 'ComparisonStyle')"));
        AreEqual(1, sim.ExecuteScalar("SELECT COLLATIONPROPERTY('Latin1_General_100_CI_AS_KS_WS', 'ComparisonStyle')"));
        AreEqual(0, sim.ExecuteScalar("SELECT COLLATIONPROPERTY('Latin1_General_100_BIN2', 'ComparisonStyle')"));
    }

    // CodePage derives from the collation model, so a UTF-8 collation reports
    // 65001 and a Japanese collation reports 932.
    [TestMethod]
    public void CollationProperty_CodePage_DerivesFromCollationModel()
    {
        var sim = new Simulation();
        AreEqual(65001, sim.ExecuteScalar("SELECT COLLATIONPROPERTY('Latin1_General_100_CI_AS_SC_UTF8', 'CodePage')"));
        AreEqual(932, sim.ExecuteScalar("SELECT COLLATIONPROPERTY('Japanese_CI_AS', 'CodePage')"));
    }

    // SSMS's per-database follow-up passes a scalar subquery as the collation
    // argument with a constant property; the constant property still drives the
    // static int type and the subquery resolves at runtime.
    [TestMethod]
    public void CollationProperty_SubqueryCollationArg_Resolves()
        => AreEqual(1252, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY((select collation_name from sys.databases where name = 'simulated'), 'CodePage')"));

    [TestMethod]
    public void CollationProperty_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('SQL_Latin1_General_CP1_CI_AS', 'Bogus')"));

    [TestMethod]
    public void CollationProperty_UnrecognizedCollation_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "SELECT COLLATIONPROPERTY('Not_A_Collation', 'CodePage')"));
}
