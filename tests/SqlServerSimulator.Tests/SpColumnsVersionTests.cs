using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>sp_columns</c> versions beside <c>_100</c> — <c>_90</c>'s down-level
/// types and <c>_170</c>'s <c>json</c> and <c>vector</c> rows — the
/// <c>@NameScope</c> the later two take, and the parameter binder's refusals
/// the ODBC catalog procedures report. Every asserted row was probed against
/// SQL Server 2025 (2026-10-16).
/// </summary>
[TestClass]
public sealed class SpColumnsVersionTests
{
    private static Simulation Fixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.j (j json, i int not null, v vector(3), dt date, tm time(4), g geography);
            create table dbo.sp (id int not null, x int sparse null, y varchar(10) sparse null, cs xml column_set for all_sparse_columns);
            """);
        return sim;
    }

    private static List<string> Rows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(4, 12).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToInvariantString())) + "|" + reader.GetValue(reader.FieldCount - 1).ToInvariantString());
        return rows;
    }

    [TestMethod]
    public void SpColumns170_ListsJsonAndVectorUnderOdbc3()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[]
        {
            "-157|json|0|0|NULL|NULL|1|NULL|NULL|-157|NULL|0|244",
            "4|int|10|4|0|10|0|NULL|NULL|4|NULL|NULL|56",
            "-156|vector|NULL|20|0|NULL|1|NULL|NULL|-156|NULL|20|245",
            "91|date|10|6|0|NULL|1|NULL|NULL|9|1|NULL|0",
        }, Rows(sim, "exec sp_columns_170 'j', @column_name = '[ijvd]%'"));
        CollectionAssert.AreEqual(new[] { "4|int|10|4|0|10|0|NULL|NULL|4|NULL|NULL|56", "9|date|10|6|0|NULL|1|NULL|NULL|9|1|NULL|0" },
            Rows(sim, "exec sp_columns_170 'j', @column_name = '[ijvd]%', @ODBCVer = 2"));
        CollectionAssert.AreEqual(new[] { "4|int|10|4|0|10|0|NULL|NULL|4|NULL|NULL|56", "91|date|10|6|0|NULL|1|NULL|NULL|9|1|NULL|0" },
            Rows(sim, "exec sp_columns_100 'j', @column_name = '[ijvd]%', @ODBCVer = 3"));
    }

    [TestMethod]
    public void SpColumns90_ReadsTheNewerTypesDownLevel()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[]
        {
            "4|int|10|4|0|10|0|NULL|NULL|4|NULL|NULL|56",
            "-9|date|10|20|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|0",
            "-9|time|13|26|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|0",
            "-3|geography|0|-1|NULL|NULL|1|NULL|NULL|-3|NULL|0|23",
        }, Rows(sim, "exec sp_columns_90 'j', @ODBCVer = 3"));
        using var reader = sim.ExecuteReader("exec sp_columns_90 'j'");
        AreEqual(25, reader.FieldCount);
        AreEqual("SS_UDT_CATALOG_NAME", reader.GetName(18));
    }

    [TestMethod]
    public void SpColumns100_TakesTheNameScopeAheadOfTheOdbcVersion()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[] { "id", "cs" }, sim.ExecuteReader("exec sp_columns_100 'sp'").EnumerateRecords().Select(r => r.GetString(3)).ToArray());
        CollectionAssert.AreEqual(new[] { "x" }, sim.ExecuteReader("exec sp_columns_100 'sp', null, null, 'x%', 3").EnumerateRecords().Select(r => r.GetString(3)).ToArray());
        CollectionAssert.AreEqual(new[] { "id", "x", "y", "cs" }, sim.ExecuteReader("exec sp_columns_170 'sp', @NameScope = 2").EnumerateRecords().Select(r => r.GetString(3)).ToArray());
        IsEmpty(sim.ExecuteReader("exec sp_columns_100 'sp', @NameScope = null").EnumerateRecords().ToArray());
        IsEmpty(sim.ExecuteReader("exec sp_columns_100 'sp', @NameScope = 1").EnumerateRecords().ToArray());
        CollectionAssert.AreEqual(new[] { "91|date|10|6|0|NULL|1|NULL|NULL|9|1|NULL|0" }, Rows(sim, "exec sp_columns_100 'j', null, null, 'dt', 0, 3"));
    }

    private static List<string> AllRows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i) is byte[] bytes ? "0x" + Convert.ToHexString(bytes) : reader.GetValue(i).ToInvariantString())));
        return rows;
    }

    [TestMethod]
    public void SpDatatypeInfo_VersionsAndAliasTypes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type dbo.phone from varchar(12) not null; create type dbo.amt from decimal(10,2) null; create type dbo.nm from nvarchar(30) not null");
        var v100 = AllRows(sim, "exec sp_datatype_info_100 @ODBCVer = 3");
        var decimalAt = v100.FindIndex(row => row.StartsWith("decimal|", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[]
        {
            "decimal|3|38|NULL|NULL|precision,scale|1|0|2|0|0|0|decimal|0|38|3|NULL|10|NULL|24",
            "amt|3|10|NULL|NULL|NULL|1|0|2|0|0|0|amt|2|2|3|NULL|10|NULL|258",
            "money|3|19|$|NULL|NULL|1|0|2|0|1|0|money|4|4|3|NULL|10|NULL|11",
        }, v100.GetRange(decimalAt, 3));
        Contains("phone|12|12|'|'|NULL|0|0|3|NULL|0|NULL|phone|NULL|NULL|12|NULL|NULL|NULL|257", v100);
        CollectionAssert.AreEqual(new[] { "nvarchar", "sysname", "nm", "date", "time" },
            AllRows(sim, "exec sp_datatype_info -9").Select(row => row.Split('|')[0]).ToArray()[..5]);
        CollectionAssert.AreEqual(new[] { "json|-157", "vector|-156", "datetimeoffset|-155" },
            AllRows(sim, "exec sp_datatype_info_170").Select(row => string.Join("|", row.Split('|')[..2])).ToArray()[..3]);
        AreEqual("datetimeoffset|-155", string.Join("|", AllRows(sim, "exec sp_datatype_info_170 @ODBCVer = 2")[0].Split('|')[..2]));
        AreEqual("xml|-152|0|N'|'|NULL|1|1|0|NULL|0|NULL|xml|NULL|NULL|-152|NULL|NULL|NULL|0", AllRows(sim, "exec sp_datatype_info_90")[0]);
    }

    [TestMethod]
    public void SpSprocColumns_VersionsListEveryGroupNumber()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create xml schema collection dbo.xsc as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"a\" type=\"xs:int\"/></xs:schema>'",
            "create procedure dbo.more @rv rowversion, @tx xml(dbo.xsc), @ge geometry, @d datetime2(4) as select 1",
            "create procedure dbo.more;2 @two int as select 2");
        var v100 = AllRows(sim, "exec sp_sproc_columns_100 'more'");
        CollectionAssert.AreEqual(new[]
        {
            "simulated|dbo|more;1|@rv|1|-2|timestamp|8|8|NULL|NULL|1|NULL|NULL|-2|NULL|8|1|YES|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|45",
            "simulated|dbo|more;1|@tx|1|-152|xml|0|0|NULL|NULL|1|NULL|NULL|-152|NULL|0|2|YES|NULL|NULL|NULL|NULL|NULL|simulated|dbo|xsc|0",
        }, v100.GetRange(1, 2));
        StartsWith("simulated|dbo|more;1|@ge|1|-151|geometry|0|0|NULL|NULL|1|NULL|NULL|-151|NULL|0|3|YES|simulated|sys|simulated|sys|Microsoft.SqlServer.Types.SqlGeometry", v100[3]);
        CollectionAssert.AreEqual(new[] { "simulated|dbo|more;2|@RETURN_VALUE", "simulated|dbo|more;2|@two" }, v100[^2..].Select(row => string.Join("|", row.Split('|')[..4])).ToArray());
        var v90 = AllRows(sim, "exec sp_sproc_columns_90 'more'");
        StartsWith("simulated|dbo|more;1|@ge|1|-3|geometry|0|-1|NULL|NULL|1|NULL|NULL|-3|NULL|0|3|YES|simulated|sys|Microsoft.SqlServer.Types.SqlGeometry", v90[3]);
        AreEqual("simulated|dbo|more;1|@d|1|-9|datetime2|24|48|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|4|YES|NULL|NULL|NULL|NULL|NULL|NULL|0", v90[4]);
    }

    [TestMethod]
    public void SpTableCollations_VersionsCollateWhatTheirClientsReadAsStrings()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.c (id int, x int sparse null, cs xml column_set for all_sparse_columns, d date, s varchar(5) collate Latin1_General_CS_AS)");
        const string Default = "0x0904D00034|SQL_Latin1_General_CP1_CI_AS";
        CollectionAssert.AreEqual(new[] { "1|id|NULL|NULL", "3|cs|NULL|NULL", "4|d|NULL|NULL", "5|s|0x0904C00000|Latin1_General_CS_AS" }, AllRows(sim, "exec sp_tablecollations_100 'dbo.c'"));
        CollectionAssert.AreEqual(new[] { "1|id|NULL|NULL", "3|cs|NULL|NULL", "4|d|" + Default, "5|s|0x0904C00000|Latin1_General_CS_AS" }, AllRows(sim, "exec sp_tablecollations_90 'dbo.c'"));
        CollectionAssert.AreEqual(new[] { "1|id|NULL|NULL", "3|cs|" + Default, "4|d|" + Default, "5|s|0x0904C00000|Latin1_General_CS_AS" }, AllRows(sim, "exec sp_tablecollations 'dbo.c'"));
    }

    [TestMethod]
    [DataRow("exec sp_columns_100 'sp', @Bogus = 1", 8145, "sp_columns_100")]
    [DataRow("exec sp_columns_90 'sp', @NameScope = 1", 8145, "sp_columns_90")]
    [DataRow("exec sp_columns 'sp', null, null, null, 3, 1", 8144, "sp_columns")]
    [DataRow("exec sp_columns_100 'sp', null, null, null, 0, 3, 1, 9", 8144, "sp_columns_100")]
    [DataRow("exec sp_tables @Bogus = 1", 8145, "sp_tables")]
    [DataRow("exec sp_pkeys 'sp', null, null, 4", 8144, "sp_pkeys")]
    [DataRow("exec sp_fkeys 'sp', @Bogus = 1", 8145, "sp_fkeys")]
    [DataRow("exec sp_stored_procedures null, null, null, 1, 2", 8144, "sp_stored_procedures")]
    [DataRow("exec sp_statistics 'sp', @Bogus = 1", 8145, "sp_statistics")]
    [DataRow("exec sp_special_columns 'sp', @Bogus = 1", 8145, "sp_special_columns")]
    [DataRow("exec sp_sproc_columns 'p', @Bogus = 1", 8145, "sp_sproc_columns")]
    [DataRow("exec sp_table_privileges 'sp', @Bogus = 1", 8145, "sp_table_privileges")]
    [DataRow("exec sp_datatype_info 1, 2, 3", 8144, "sp_datatype_info")]
    [DataRow("exec sp_server_info @Bogus = 1", 8145, "sp_server_info")]
    [DataRow("exec sp_databases 1", 8146, "sp_databases")]
    [DataRow("exec sp_helprotect @Bogus = 1", 8145, "sp_helprotect")]
    [DataRow("exec sp_MSforeachtable 'select 1', @Bogus = 1", 8145, "sp_MSforeachtable")]
    public void CatalogProcedures_RefuseTheirArgumentsAsTheBinderDoes(string call, int number, string procedure)
    {
        var error = Fixture().AssertSqlError(call, number).Errors[0];
        AreEqual((procedure, 0), (error.Procedure, error.LineNumber));
    }
}
