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
