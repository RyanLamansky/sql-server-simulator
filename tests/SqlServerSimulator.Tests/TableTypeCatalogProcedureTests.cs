using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The catalog procedures over user-defined table types: ODBC's
/// <c>sp_table_types</c> / <c>sp_table_type_columns_100</c> /
/// <c>sp_table_type_pkeys</c> and OLE DB's three <c>_rowset</c> forms. Every
/// asserted value was probed against SQL Server 2025 (2026-10-16) with the
/// same fixture.
/// </summary>
[TestClass]
public sealed class TableTypeCatalogProcedureTests
{
    private static Simulation Fixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create schema s2",
            """
            create type dbo.TT1 as table (id int primary key, name nvarchar(50) null, amt decimal(10,2) not null default 0, d datetime2(3), x xml, v varchar(max));
            create type dbo.TT2 as table (a int not null, b varchar(10) not null, c as a*2, primary key (b, a));
            create type s2.TT1 as table (k bigint identity(1,1), g uniqueidentifier, unique(g));
            create type dbo.Zeta as table (q tinyint);
            create type dbo.aLow as table (q tinyint);
            create table dbo.realtab (id int primary key);
            """);
        return sim;
    }

    /// <summary>Every result set the command returns, each row its cells rendered invariantly, NULL as "NULL".</summary>
    private static List<List<string>> ResultSets(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        var sets = new List<List<string>>();
        do
        {
            var rows = new List<string>();
            while (reader.Read())
            {
                var cells = new string[reader.FieldCount];
                for (var i = 0; i < cells.Length; i++)
                {
                    cells[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i) switch
                    {
                        byte[] bytes => "0x" + Convert.ToHexString(bytes),
                        bool flag => flag ? "1" : "0",
                        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                        var value => value.ToString()!,
                    };
                }
                rows.Add(string.Join("|", cells));
            }
            sets.Add(rows);
        }
        while (reader.NextResult());
        return sets;
    }

    private static List<string> Rows(Simulation sim, string sql) => ResultSets(sim, sql)[0];

    /// <summary>The cells <paramref name="columns"/> picks from each row, by ordinal.</summary>
    private static List<string> Cells(List<string> rows, params int[] columns) =>
        rows.ConvertAll(row => string.Join("|", columns.Select(i => row.Split('|')[i])));

    private static string Columns(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        return string.Join(" ", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}:{reader.GetDataTypeName(i)}"));
    }

    [TestMethod]
    public void TableTypes_ListsEveryTypeByOwnerAndName()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(
            new[] { "simulated|dbo|aLow|TABLE TYPE|NULL", "simulated|dbo|TT1|TABLE TYPE|NULL", "simulated|dbo|TT2|TABLE TYPE|NULL", "simulated|dbo|Zeta|TABLE TYPE|NULL", "simulated|s2|TT1|TABLE TYPE|NULL" },
            Rows(sim, "exec sp_table_types"));
        AreEqual("TABLE_QUALIFIER:nvarchar TABLE_OWNER:nvarchar TABLE_NAME:nvarchar TABLE_TYPE:varchar REMARKS:varchar", Columns(sim, "exec sp_table_types"));
    }

    [TestMethod]
    [DataRow("exec sp_table_types 'TT%'", "dbo.TT1 dbo.TT2 s2.TT1")]
    [DataRow("exec sp_table_types @table_owner = 's%'", "s2.TT1")]
    [DataRow("exec sp_table_types 'TT_', 'db_', @fUsePattern = 0", "dbo.TT1 dbo.TT2")]
    [DataRow("exec sp_table_types 'tt1', null, 'SIMULATED', '''VIEW'''", "dbo.TT1 s2.TT1")]
    [DataRow("exec sp_table_types 'TT1', null, ''", "")]
    [DataRow("exec sp_table_types 'nomatch'", "")]
    public void TableTypes_NamesArePatterns_AndTheTypeListChangesNothing(string sql, string expected)
        => AreEqual(expected, string.Join(" ", Cells(Rows(Fixture(), sql), 1, 2).Select(cell => cell.Replace('|', '.'))));

    [TestMethod]
    [DataRow("sp_table_types 'TT1', null, 'other'", "sp_table_types", 26)]
    [DataRow("sp_table_types 'TT1', null, '%'", "sp_table_types", 26)]
    [DataRow("sp_table_type_columns_100 'TT1', null, ''", "sp_table_type_columns_100", 22)]
    [DataRow("sp_table_type_pkeys 'TT1', null, 'other'", "sp_table_type_pkeys", 13)]
    public void AQualifierNamingAnotherDatabase_IsMsg15250(string exec, string procedure, int line)
    {
        var error = Fixture().AssertSqlError("exec " + exec, 15250).Errors[0];
        AreEqual((procedure, line), (error.Procedure, error.LineNumber));
    }

    [TestMethod]
    [DataRow("sp_table_types 'TT1', null, 'other'", -6)]
    [DataRow("sp_table_type_pkeys 'TT1', null, 'other'", -6)]
    [DataRow("sp_table_type_pkeys", -5)]
    [DataRow("sp_table_types", 0)]
    public void ReturnCodes(string exec, int expected)
    {
        // The leading result set opens the reader, so the procedure's error
        // surfaces from NextResult, past which the batch goes on.
        using var connection = Fixture().CreateOpenConnection();
        using var command = connection.CreateCommand($"select 0 opened; declare @r int = -5; exec @r = {exec}; select @r r");
        using var reader = command.ExecuteReader();
        int? returned = null;
        for (var advanced = true; advanced && returned is null;)
        {
            try
            {
                advanced = reader.NextResult();
                if (advanced && reader.GetName(0) == "r" && reader.Read())
                    returned = reader.GetInt32(0);
            }
            catch (SimulatedSqlException)
            {
            }
        }
        AreEqual(expected, returned);
    }

    [TestMethod]
    public void TableTypeColumns_DescribeEachColumnAsSpColumns100Does()
    {
        var rows = Rows(Fixture(), "exec sp_table_type_columns_100 'TT1', 'dbo'");
        CollectionAssert.AreEqual(
            new[]
            {
                "simulated|dbo|TT1|id|4|int|10|4|0|10|0|NULL|NULL|4|NULL|NULL|1|NO|0|0|0|0|NULL|NULL|NULL|NULL|NULL|NULL|56",
                "simulated|dbo|TT1|name|-9|nvarchar|50|100|NULL|NULL|1|NULL|NULL|-9|NULL|100|2|YES|0|0|0|0|NULL|NULL|NULL|NULL|NULL|NULL|39",
                "simulated|dbo|TT1|amt|3|decimal|10|12|2|10|0|NULL|((0))|3|NULL|NULL|3|NO|0|0|0|0|NULL|NULL|NULL|NULL|NULL|NULL|55",
                "simulated|dbo|TT1|d|11|datetime2|23|16|3|NULL|1|NULL|NULL|9|3|NULL|4|YES|0|0|0|0|NULL|NULL|NULL|NULL|NULL|NULL|0",
                "simulated|dbo|TT1|x|-152|xml|0|0|NULL|NULL|1|NULL|NULL|-152|NULL|0|5|YES|0|0|0|0|NULL|NULL|NULL|NULL|NULL|NULL|0",
                "simulated|dbo|TT1|v|12|varchar|0|0|NULL|NULL|1|NULL|NULL|12|NULL|0|6|YES|0|0|0|0|NULL|NULL|NULL|NULL|NULL|NULL|39",
            },
            rows);
    }

    [TestMethod]
    [DataRow("exec sp_table_type_columns_100 null", 13)]
    [DataRow("exec sp_table_type_columns_100 'T%', null, null, 'a%'", 2)]
    [DataRow("exec sp_table_type_columns_100 'TT_', @fUsePattern = 0", 0)]
    [DataRow("exec sp_table_type_columns_100 'TT1', 'dbo', null, 'n_me', @fUsePattern = 0", 0)]
    [DataRow("exec sp_table_type_columns_100 'TT1', 'dbo', null, 'n_me', @fUsePattern = null", 1)]
    [DataRow("exec sp_table_type_columns_100 'realtab'", 0)]
    public void TableTypeColumns_NamesArePatternsUnlessfUsePatternIsZero(string sql, int expected)
        => HasCount(expected, Rows(Fixture(), sql));

    [TestMethod]
    [DataRow(3, "93")]
    [DataRow(2, "11")]
    [DataRow(4, "11")]
    public void TableTypeColumns_OnlyOdbc3ReadsOdbc3sDateCodes(int odbcVer, string dataType)
        => AreEqual(dataType, Cells(Rows(Fixture(), $"exec sp_table_type_columns_100 'TT1', @column_name = 'd', @ODBCVer = {odbcVer}"), 4)[0]);

    [TestMethod]
    public void TableTypeColumns_RequiresTheName()
        => AreEqual((0, "sp_table_type_columns_100"), Fixture().AssertSqlError("exec sp_table_type_columns_100", 201).Errors[0] is var e ? (e.LineNumber, e.Procedure) : default);

    [TestMethod]
    public void TableTypePkeys_ListsKeyColumnsInKeyOrder()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[] { "dbo|TT1|id|1", "dbo|TT2|b|1", "dbo|TT2|a|2" }, Cells(Rows(sim, "exec sp_table_type_pkeys null"), 1, 2, 3, 4));
        CollectionAssert.AreEqual(new[] { "TT2|b|1", "TT2|a|2" }, Cells(Rows(sim, "exec sp_table_type_pkeys 'tt2', 'DBO'"), 2, 3, 4));
        IsEmpty(Rows(sim, "exec sp_table_type_pkeys 'TT%'"));
        IsEmpty(Rows(sim, "exec sp_table_type_pkeys 'TT1', 's2'"));
        AreEqual("TABLE_QUALIFIER:nvarchar TABLE_OWNER:nvarchar TABLE_NAME:nvarchar COLUMN_NAME:nvarchar KEY_SEQ:int PK_NAME:nvarchar", Columns(sim, "exec sp_table_type_pkeys 'TT2'"));
        CollectionAssert.AreEqual(new[] { true, true, false, true, true, true }, sim.ColumnNullability("exec sp_table_type_pkeys 'TT2'"));
        StartsWith("PK__TT_TT2_", Cells(Rows(sim, "exec sp_table_type_pkeys 'TT2'"), 5)[0]);
    }

    [TestMethod]
    public void TableTypesRowset_ListsExactNames()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[] { "dbo|aLow", "dbo|TT1", "dbo|TT2", "dbo|Zeta", "s2|TT1" }, Cells(Rows(sim, "exec sp_table_types_rowset"), 1, 2));
        CollectionAssert.AreEqual(new[] { "s2|TT1|TABLE TYPE" }, Cells(Rows(sim, "exec sp_table_types_rowset 'TT1', 's2'"), 1, 2, 3));
        IsEmpty(Rows(sim, "exec sp_table_types_rowset 'TT%'"));
        AreEqual(
            "TABLE_CATALOG:nvarchar TABLE_SCHEMA:nvarchar TABLE_NAME:nvarchar TABLE_TYPE:nvarchar TABLE_GUID:uniqueidentifier DESCRIPTION:nvarchar TABLE_PROPID:int DATE_CREATED:datetime DATE_MODIFIED:datetime",
            Columns(sim, "exec sp_table_types_rowset"));
        CollectionAssert.AreEqual(new[] { true, true, false, true, true, true, true, false, true }, sim.ColumnNullability("exec sp_table_types_rowset"));
    }

    [TestMethod]
    public void TableTypePrimaryKeysRowset_ListsKeyColumnsByName()
        => CollectionAssert.AreEqual(
            new[] { "TT1|id|1", "TT2|a|2", "TT2|b|1" },
            Cells(Rows(Fixture(), "exec sp_table_type_primary_keys_rowset"), 2, 3, 6));

    [TestMethod]
    public void TableTypeColumnsRowset_DescribesEachColumnAsTheColumnsRowsetDoes()
    {
        var rows = Rows(Fixture(), "exec sp_table_type_columns_100_rowset 'TT2', 'dbo', 'b'");
        AreEqual(
            "simulated|dbo|TT2|b|NULL|NULL|2|0|NULL|4|0|129|NULL|10|10|NULL|NULL|NULL|master|dbo|iso_1|master|dbo|SQL_Latin1_General_CP1_CI_AS|NULL|NULL|NULL|NULL|1033|196609|52|0x0904D00034|0|NULL|NULL|NULL|NULL|NULL|NULL|NULL|0|0",
            rows.Single());
        HasCount(13, Rows(Fixture(), "exec sp_table_type_columns_100_rowset"));
    }

    [TestMethod]
    public void ARestrictedPrincipal_SeesTheTypesItHoldsPermissionsOn_AndOnlyDefinitionsShowColumnsInTheRowset()
    {
        var sim = Fixture();
        sim.ExecuteBatches("""
            create user u without login;
            grant execute on type::dbo.TT2 to u;
            grant view definition on type::dbo.TT1 to u;
            grant references on type::dbo.Zeta to u;
            grant alter on schema::s2 to u;
            """);
        CollectionAssert.AreEqual(new[] { "dbo|TT1", "dbo|TT2", "dbo|Zeta", "s2|TT1" }, Cells(Rows(sim, "execute as user = 'u'; exec sp_table_types; revert"), 1, 2));
        CollectionAssert.AreEqual(new[] { "TT1" }, Rows(sim, "execute as user = 'u'; exec sp_table_type_columns_100_rowset; revert").Select(row => row.Split('|')[2]).Distinct().ToArray());
        CollectionAssert.AreEqual(new[] { "TT1", "TT2", "Zeta", "TT1" }, Rows(sim, "execute as user = 'u'; select name from sys.table_types order by schema_id, name; revert"));
    }
}
