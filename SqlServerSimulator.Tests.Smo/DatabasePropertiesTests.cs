using Microsoft.SqlServer.Management.Smo;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The Database Properties dialog surface: SMO reading a database's filegroups,
/// files, and database-scoped configurations. These are the shapes SSMS's
/// property-bag drain reaches once the earlier catalog layers resolve — the
/// oracle for the wire-manifested fixes (varbinary→sysname filegroup-name
/// decode, the nvarchar database_scoped_configurations value type,
/// sys.database_files.drop_lsn).
/// </summary>
[TestClass]
public sealed class DatabasePropertiesTests
{
    private static Server server = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _) => server = SmoFixture.NewServer();

    [ClassCleanup]
    public static void ClassCleanup() => server.ConnectionContext.Disconnect();

    private static Database FixtureDatabase => server.Databases[SmoFixture.DatabaseName];

    // SMO's FileGroup enumeration filters on
    // CAST(CAST(g.name AS varbinary(256)) AS sysname); before the sysname
    // decode fix the name came back as interleaved-NUL garbage and the PRIMARY
    // lookup missed (and any URN build threw).
    [TestMethod]
    public void FileGroups_PrimaryResolvesByName()
    {
        var fileGroups = FixtureDatabase.FileGroups;
        IsGreaterThanOrEqualTo(1, fileGroups.Count);
        IsNotNull(fileGroups["PRIMARY"]);
    }

    // The FileGroup→Files enumeration filters on `df.drop_lsn is null`;
    // sys.database_files was missing that column.
    [TestMethod]
    public void FileGroups_PrimaryFilesEnumerate()
    {
        var primary = FixtureDatabase.FileGroups["PRIMARY"];
        IsNotNull(primary);
        IsGreaterThanOrEqualTo(1, primary.Files.Count);
    }

    // Database-scoped configurations enumerate and their names read back
    // uncorrupted (sysname over the wire).
    [TestMethod]
    public void DatabaseScopedConfigurations_EnumerateWithNames()
    {
        var configs = FixtureDatabase.DatabaseScopedConfigurations;
        IsGreaterThanOrEqualTo(1, configs.Count);
        IsNotNull(configs["MAXDOP"]);
    }

    // Reading a scoped-configuration Value drives SMO's
    // ISNULL(value_for_secondary, 'PRIMARY') projection — the string fallback
    // that a bigint value column rejected (Msg 245). It must not throw.
    [TestMethod]
    public void DatabaseScopedConfigurations_ValueReadsWithoutConversionError()
    {
        var maxDop = FixtureDatabase.DatabaseScopedConfigurations["MAXDOP"];
        IsNotNull(maxDop);
        // Value is a plain read off the projected row; the point is that it
        // does not raise a varchar→bigint conversion.
        _ = maxDop.Value;
    }
}
