using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Engine gaps surfaced by draining SSMS's Database Properties dialog property
/// bag through headless SMO (see docs/claude/backlog.md's ssms-shakedown
/// entry). Each test pins one fix; the dialog's own SMO queries are the shapes
/// that exercised them.
/// </summary>
[TestClass]
public sealed class DatabasePropertiesDrainTests
{
    private static object? Scalar(string sql) => new Simulation().ExecuteScalar(sql);

    // varbinary → sysname CAST decoded the bytes as CP1252 instead of UTF-16,
    // corrupting every round-tripped name to interleaved-NUL garbage
    // ("PRIMARY" → "P\0R\0I\0…"). SMO's FileGroup enumeration filters on
    // CAST(CAST(g.name AS varbinary(256)) AS sysname), so the filegroup name
    // came back mangled. sysname is nvarchar; it must decode UTF-16 LE.
    [TestMethod]
    public void VarbinaryToSysname_DecodesUtf16NotCp1252()
        => AreEqual("PRIMARY", Scalar("SELECT CAST(CAST(N'PRIMARY' AS varbinary(256)) AS sysname)"));

    [TestMethod]
    public void VarbinaryToSysname_DirectUtf16Bytes()
        => AreEqual("PRI", Scalar("SELECT CAST(0x500052004900 AS sysname)"));

    // varchar's CP1252 round-trip must stay intact (the decode-branch fix only
    // added sysname to the UTF-16 side).
    [TestMethod]
    public void VarbinaryToVarchar_StillCp1252()
        => AreEqual("PRIMARY", Scalar("SELECT CAST(CAST('PRIMARY' AS varbinary(256)) AS varchar(50))"));

    // SSMS emits an over-long all-zero GUID literal via
    // ISNULL(mirroring_guid, '00000000-0000-0000-0000-0000000000000000').
    // Real SQL Server parses the leading 36-char D-form and ignores the tail;
    // the simulator rejected it (Msg 8169).
    [TestMethod]
    public void UniqueIdentifier_OverLongLiteral_ParsesLeading36()
        => AreEqual(
            new Guid("00000000-0000-0000-0000-000000000000"),
            Scalar("SELECT CAST('00000000-0000-0000-0000-0000000000000000' AS uniqueidentifier)"));

    [TestMethod]
    public void UniqueIdentifier_TrailingJunk_Ignored()
        => AreEqual(
            new Guid("12345678-0000-0000-0000-000000000000"),
            Scalar("SELECT CAST('12345678-0000-0000-0000-000000000000ffff' AS uniqueidentifier)"));

    // A leading string too short for the D-form still fails.
    [TestMethod]
    public void UniqueIdentifier_TooShort_Rejected()
        => new Simulation().AssertSqlError(
            "SELECT CAST('00000000-0000-0000-0000-00000000000' AS uniqueidentifier)", 8169);

    // sys.database_scoped_configurations.value / value_for_secondary are
    // sql_variant. SSMS's Value/ValueForSecondary projection does
    // ISNULL(value_for_secondary, 'PRIMARY') — the variant NULL falls through
    // to the string fallback, and the ISNULL result stays sql_variant (its
    // first argument's type), so it reads back as the string 'PRIMARY'.
    [TestMethod]
    public void DatabaseScopedConfigurations_StringFallbackReadsAsString()
        => AreEqual("PRIMARY", Scalar(
            "SELECT ISNULL(value_for_secondary, 'PRIMARY') FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'"));

    // MAXDOP's value is a sql_variant carrying int; the non-NULL primary short-
    // circuits ISNULL and surfaces the inner int 0 (not a string).
    [TestMethod]
    public void DatabaseScopedConfigurations_IntValueReadsAsInt()
        => AreEqual(0, Scalar(
            "SELECT ISNULL(value, 'NULL') FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'"));

    // sys.database_files gained drop_lsn (sys.master_files already had it); the
    // FileGroup→Files enumeration filters on `df.drop_lsn is null`.
    [TestMethod]
    public void DatabaseFiles_HasDropLsnColumn()
        => AreEqual(DBNull.Value, Scalar("SELECT drop_lsn FROM sys.database_files WHERE file_id = 1"));

    [TestMethod]
    public void MasterFiles_DropLsnColumn_StillPresent()
        => AreEqual(DBNull.Value, Scalar("SELECT TOP 1 drop_lsn FROM sys.master_files"));

    // CROSS APPLY over an unknown table-valued function is a deferred
    // name-resolution error (Msg 208), not a parse-time syntax error (Msg 102) —
    // so an un-taken IF branch naming an unknown TVF compiles and is discarded.
    // SSMS's VolumeFreeSpace probe gates such a branch on EngineEdition = 9.
    [TestMethod]
    public void CrossApplyUnknownTvf_InUntakenBranch_Compiles()
        => AreEqual(42, Scalar(
            "IF 1 = 0 SELECT f.x FROM (SELECT 1 x) f CROSS APPLY sys.dm_os_volume_stats(1, 2); SELECT 42"));

    [TestMethod]
    public void CrossApplyUnknownTvf_WhenTaken_RaisesInvalidObjectName()
        => new Simulation().AssertSqlError(
            "SELECT f.x FROM (SELECT 1 x) f CROSS APPLY sys.dm_os_volume_stats(1, 2)", 208);

    // A bare (non-function) name after APPLY stays a genuine syntax error.
    [TestMethod]
    public void CrossApplyBareName_StillSyntaxError()
        => new Simulation().AssertSqlError(
            "SELECT f.x FROM (SELECT 1 x) f CROSS APPLY sometable", 102);
}
