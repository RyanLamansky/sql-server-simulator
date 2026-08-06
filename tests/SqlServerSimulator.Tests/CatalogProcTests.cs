using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the ODBC/JDBC catalog stored procedures <c>sp_tables</c>
/// (SQLTables) and <c>sp_columns_100</c> (SQLColumns). All asserted values are
/// probe-confirmed against SQL Server 2025 (2026-07-23).
/// </summary>
[TestClass]
public sealed class CatalogProcTests
{
    // Fixture mirroring the probe table: an identity PK, a nullable nvarchar,
    // a decimal with a DEFAULT, a scaled datetime2, and a spread of other
    // types plus a computed column and a view over two columns.
    private static Simulation NewFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table fixt (
                id int identity(1,1) not null primary key,
                name nvarchar(50) null,
                price decimal(9,2) not null default (0),
                created datetime2(3) null,
                qty smallint not null,
                code char(5) null,
                flag bit null,
                total money null,
                descr varchar(max) null,
                computed as (qty * 2)
            )
            """,
            "create view vfixt as select id, name from fixt");
        return sim;
    }

    private static List<Dictionary<string, object?>> Run(Simulation sim, string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var reader = sim.ExecuteReader(sql);
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    private static Dictionary<string, object?> Column(Simulation sim, string table, string column)
        => Run(sim, $"exec sp_columns_100 @table_name='{table}', @column_name='{column}'").Single();

    // ===== sp_tables =====

    [TestMethod]
    public void SpTables_ListsTablesAndViews()
    {
        var rows = Run(NewFixture(), "exec sp_tables @table_owner='dbo'");
        CollectionAssert.AreEqual(
            new[] { ("fixt", "TABLE"), ("vfixt", "VIEW") },
            rows.Select(r => ((string?)r["TABLE_NAME"], (string?)r["TABLE_TYPE"])).ToArray());
        AreEqual("simulated", rows[0]["TABLE_QUALIFIER"]);
        AreEqual("dbo", rows[0]["TABLE_OWNER"]);
        IsNull(rows[0]["REMARKS"]);
    }

    [TestMethod]
    public void SpTables_TableTypeFilter_SelectsViewsOnly()
    {
        // @table_type is the ODBC quoted comma-list; the value string here is
        // literally 'VIEW' (embedded single quotes), which the proc strips.
        var rows = Run(NewFixture(), "exec sp_tables @table_type=N'''VIEW'''");
        HasCount(1, rows);
        AreEqual("vfixt", rows[0]["TABLE_NAME"]);
        AreEqual("VIEW", rows[0]["TABLE_TYPE"]);
    }

    [TestMethod]
    public void SpTables_NameLikePattern()
    {
        var rows = Run(NewFixture(), "exec sp_tables @table_name='fi%'");
        HasCount(1, rows);
        AreEqual("fixt", rows[0]["TABLE_NAME"]);
    }

    [TestMethod]
    public void SpTables_QualifierMismatch_ReturnsEmpty()
        => IsEmpty(Run(NewFixture(), "exec sp_tables @table_qualifier='otherdb'"));

    // ===== sp_columns_100: per-type values (probe-confirmed) =====

    [TestMethod]
    public void SpColumns_IntIdentity()
    {
        var c = Column(NewFixture(), "fixt", "id");
        AreEqual((short)4, c["DATA_TYPE"]);
        AreEqual("int identity", c["TYPE_NAME"]);
        AreEqual(10, c["PRECISION"]);
        AreEqual(4, c["LENGTH"]);
        AreEqual((short)0, c["SCALE"]);
        AreEqual((short)10, c["RADIX"]);
        AreEqual((short)0, c["NULLABLE"]);
        AreEqual((short)4, c["SQL_DATA_TYPE"]);
        IsNull(c["SQL_DATETIME_SUB"]);
        IsNull(c["CHAR_OCTET_LENGTH"]);
        AreEqual("NO", c["IS_NULLABLE"]);
        AreEqual((short)1, c["SS_IS_IDENTITY"]);
        AreEqual((short)0, c["SS_IS_COMPUTED"]);
        AreEqual((byte)56, c["SS_DATA_TYPE"]);
        AreEqual(1, c["ORDINAL_POSITION"]);
    }

    [TestMethod]
    public void SpColumns_NVarchar()
    {
        var c = Column(NewFixture(), "fixt", "name");
        AreEqual((short)-9, c["DATA_TYPE"]);
        AreEqual("nvarchar", c["TYPE_NAME"]);
        AreEqual(50, c["PRECISION"]);
        AreEqual(100, c["LENGTH"]);
        IsNull(c["SCALE"]);
        IsNull(c["RADIX"]);
        AreEqual((short)1, c["NULLABLE"]);
        AreEqual((short)-9, c["SQL_DATA_TYPE"]);
        AreEqual(100, c["CHAR_OCTET_LENGTH"]);
        AreEqual("YES", c["IS_NULLABLE"]);
        AreEqual((byte)39, c["SS_DATA_TYPE"]);
    }

    [TestMethod]
    public void SpColumns_Decimal_WithDefault()
    {
        var c = Column(NewFixture(), "fixt", "price");
        AreEqual((short)3, c["DATA_TYPE"]);
        AreEqual("decimal", c["TYPE_NAME"]);
        AreEqual(9, c["PRECISION"]);
        AreEqual(11, c["LENGTH"]);
        AreEqual((short)2, c["SCALE"]);
        AreEqual((short)10, c["RADIX"]);
        AreEqual((short)0, c["NULLABLE"]);
        AreEqual("((0))", c["COLUMN_DEF"]);
        AreEqual((byte)55, c["SS_DATA_TYPE"]);
    }

    [TestMethod]
    public void SpColumns_DateTime2_V2Codes()
    {
        var c = Column(NewFixture(), "fixt", "created");
        AreEqual((short)11, c["DATA_TYPE"]);
        AreEqual("datetime2", c["TYPE_NAME"]);
        AreEqual(23, c["PRECISION"]);
        AreEqual(16, c["LENGTH"]);
        AreEqual((short)3, c["SCALE"]);
        AreEqual((short)9, c["SQL_DATA_TYPE"]);
        AreEqual((short)3, c["SQL_DATETIME_SUB"]);
        AreEqual((byte)0, c["SS_DATA_TYPE"]);
    }

    [TestMethod]
    public void SpColumns_DateTime2_V3CodeShift()
    {
        // @ODBCVer >= 3 shifts the temporal DATA_TYPE to the verbatim 93 code,
        // exactly as sp_datatype_info_100 does; SQL_DATA_TYPE stays 9.
        var c = Run(NewFixture(),
            "exec sp_columns_100 @table_name='fixt', @column_name='created', @ODBCVer=3").Single();
        AreEqual((short)93, c["DATA_TYPE"]);
        AreEqual((short)9, c["SQL_DATA_TYPE"]);
    }

    [TestMethod]
    public void SpColumns_Money()
    {
        var c = Column(NewFixture(), "fixt", "total");
        AreEqual((short)3, c["DATA_TYPE"]);
        AreEqual("money", c["TYPE_NAME"]);
        AreEqual(19, c["PRECISION"]);
        AreEqual(21, c["LENGTH"]);
        AreEqual((short)4, c["SCALE"]);
        AreEqual((short)10, c["RADIX"]);
        AreEqual((byte)110, c["SS_DATA_TYPE"]);
    }

    [TestMethod]
    public void SpColumns_Char()
    {
        var c = Column(NewFixture(), "fixt", "code");
        AreEqual((short)1, c["DATA_TYPE"]);
        AreEqual("char", c["TYPE_NAME"]);
        AreEqual(5, c["PRECISION"]);
        AreEqual(5, c["LENGTH"]);
        AreEqual(5, c["CHAR_OCTET_LENGTH"]);
        AreEqual((byte)39, c["SS_DATA_TYPE"]);
    }

    [TestMethod]
    public void SpColumns_Bit()
    {
        var c = Column(NewFixture(), "fixt", "flag");
        AreEqual((short)-7, c["DATA_TYPE"]);
        AreEqual("bit", c["TYPE_NAME"]);
        AreEqual(1, c["PRECISION"]);
        AreEqual(1, c["LENGTH"]);
        IsNull(c["SCALE"]);
        IsNull(c["RADIX"]);
        AreEqual((byte)50, c["SS_DATA_TYPE"]);
    }

    [TestMethod]
    public void SpColumns_VarcharMax()
    {
        var c = Column(NewFixture(), "fixt", "descr");
        AreEqual((short)12, c["DATA_TYPE"]);
        AreEqual("varchar", c["TYPE_NAME"]);
        AreEqual(0, c["PRECISION"]);
        AreEqual(0, c["LENGTH"]);
        AreEqual(0, c["CHAR_OCTET_LENGTH"]);
    }

    [TestMethod]
    public void SpColumns_ComputedColumn()
    {
        var c = Column(NewFixture(), "fixt", "computed");
        AreEqual((short)4, c["DATA_TYPE"]);
        AreEqual("int", c["TYPE_NAME"]);
        AreEqual((short)1, c["SS_IS_COMPUTED"]);
        AreEqual((short)0, c["SS_IS_IDENTITY"]);
    }

    [TestMethod]
    public void SpColumns_AllColumnsInOrdinalOrder()
    {
        var rows = Run(NewFixture(), "exec sp_columns_100 @table_name='fixt'");
        CollectionAssert.AreEqual(
            new[] { "id", "name", "price", "created", "qty", "code", "flag", "total", "descr", "computed" },
            rows.Select(r => (string?)r["COLUMN_NAME"]).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 10).Cast<object?>().ToArray(),
            rows.Select(r => r["ORDINAL_POSITION"]).ToArray());
    }

    [TestMethod]
    public void SpColumns_ViewColumns()
    {
        var rows = Run(NewFixture(), "exec sp_columns_100 @table_name='vfixt'");
        CollectionAssert.AreEqual(
            new[] { "id", "name" },
            rows.Select(r => (string?)r["COLUMN_NAME"]).ToArray());
        // A view column is never identity / computed.
        AreEqual((short)0, rows[0]["SS_IS_IDENTITY"]);
    }

    [TestMethod]
    public void SpColumns_TwentyNineColumnShape()
    {
        using var reader = NewFixture().ExecuteReader("exec sp_columns_100 @table_name='fixt'");
        AreEqual(29, reader.FieldCount);
        AreEqual("TABLE_QUALIFIER", reader.GetName(0));
        AreEqual("SS_DATA_TYPE", reader.GetName(28));
    }

    // Fixture with a composite (unnamed) clustered PRIMARY KEY, a unique
    // nonclustered index, a non-unique DESC nonclustered index, three rows,
    // and a stored procedure — the shapes sp_pkeys / sp_statistics_100 /
    // sp_stored_procedures project.
    private static Simulation NewIndexedFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table cust (
                custid int not null,
                regionid int not null,
                name nvarchar(50) null,
                primary key (custid, regionid)
            )
            """,
            "create unique nonclustered index ux_cust_region on cust (regionid)",
            "create nonclustered index ix_cust_name on cust (name desc)",
            "create procedure myproc @a int, @b int output as select 1");
        _ = sim.ExecuteNonQuery("insert cust (custid, regionid, name) values (1, 10, 'a'), (2, 20, 'b'), (3, 30, null)");
        return sim;
    }

    // ===== sp_pkeys =====

    [TestMethod]
    public void SpPkeys_ReturnsKeyColumnsInOrder()
    {
        var rows = Run(NewIndexedFixture(), "exec sp_pkeys @table_name='cust'");
        HasCount(2, rows);
        AreEqual("simulated", rows[0]["TABLE_QUALIFIER"]);
        AreEqual("dbo", rows[0]["TABLE_OWNER"]);
        AreEqual("cust", rows[0]["TABLE_NAME"]);
        CollectionAssert.AreEqual(
            new (string?, short?)[] { ("custid", 1), ("regionid", 2) },
            rows.Select(r => ((string?)r["COLUMN_NAME"], (short?)r["KEY_SEQ"])).ToArray());
        // The auto-generated PK name is PK__<table8>__<hex>; real's is
        // object-id-derived, so assert the shape, not the exact hex.
        StartsWith("PK__", (string)rows[0]["PK_NAME"]!);
        AreEqual(rows[0]["PK_NAME"], rows[1]["PK_NAME"]);
    }

    [TestMethod]
    public void SpPkeys_NoPrimaryKey_ReturnsEmpty()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table nopk (a int null)");
        IsEmpty(Run(sim, "exec sp_pkeys @table_name='nopk'"));
    }

    [TestMethod]
    public void SpPkeys_ExactMatch_WildcardMatchesNothing()
        => IsEmpty(Run(NewIndexedFixture(), "exec sp_pkeys @table_name='cus%'"));

    // ===== sp_statistics_100 =====

    [TestMethod]
    public void SpStatistics_SummaryRowFirst()
    {
        var rows = Run(NewIndexedFixture(), "exec sp_statistics_100 @table_name='cust'");
        var summary = rows[0];
        AreEqual((short)0, summary["TYPE"]);                 // SQL_TABLE_STAT
        IsNull(summary["NON_UNIQUE"]);
        IsNull(summary["INDEX_QUALIFIER"]);
        IsNull(summary["INDEX_NAME"]);
        IsNull(summary["SEQ_IN_INDEX"]);
        IsNull(summary["COLUMN_NAME"]);
        IsNull(summary["COLLATION"]);
        AreEqual(3, summary["CARDINALITY"]);                 // live row count
        IsNotNull(summary["PAGES"]);
    }

    [TestMethod]
    public void SpStatistics_NoIndexName_SummaryRowOnly()
    {
        // @index_name is a LIKE pattern; NULL / omitted yields the summary row
        // alone (probe-confirmed against SQL Server 2025).
        var rows = Run(NewIndexedFixture(), "exec sp_statistics_100 @table_name='cust'");
        HasCount(1, rows);
        AreEqual((short)0, rows[0]["TYPE"]);
    }

    [TestMethod]
    public void SpStatistics_IndexRows()
    {
        // JDBC getIndexInfo passes @index_name='%' to get every index.
        var rows = Run(NewIndexedFixture(), "exec sp_statistics_100 @table_name='cust', @index_name='%'");
        // Summary row + two clustered-PK columns + one unique NC + one non-unique NC.
        HasCount(5, rows);

        var pkCustid = rows[1];
        AreEqual((short)0, pkCustid["NON_UNIQUE"]);          // PK is unique
        AreEqual("cust", pkCustid["INDEX_QUALIFIER"]);       // table name, not schema
        StartsWith("PK__", (string)pkCustid["INDEX_NAME"]!);
        AreEqual((short)1, pkCustid["TYPE"]);                // SQL_INDEX_CLUSTERED
        AreEqual((short)1, pkCustid["SEQ_IN_INDEX"]);
        AreEqual("custid", pkCustid["COLUMN_NAME"]);
        AreEqual("A", pkCustid["COLLATION"]);
        AreEqual(3, pkCustid["CARDINALITY"]);                // clustered carries table cardinality
        AreEqual(rows[0]["PAGES"], pkCustid["PAGES"]);       // same page count as the summary

        var pkRegion = rows[2];
        AreEqual((short)2, pkRegion["SEQ_IN_INDEX"]);
        AreEqual("regionid", pkRegion["COLUMN_NAME"]);

        var uxRegion = rows[3];
        AreEqual((short)0, uxRegion["NON_UNIQUE"]);          // unique index
        AreEqual("ux_cust_region", uxRegion["INDEX_NAME"]);
        AreEqual((short)3, uxRegion["TYPE"]);                // SQL_INDEX_OTHER (nonclustered)
        AreEqual("regionid", uxRegion["COLUMN_NAME"]);
        AreEqual("A", uxRegion["COLLATION"]);
        IsNull(uxRegion["CARDINALITY"]);                     // nonclustered: no stats
        IsNull(uxRegion["PAGES"]);

        var ixName = rows[4];
        AreEqual((short)1, ixName["NON_UNIQUE"]);            // non-unique index
        AreEqual("ix_cust_name", ixName["INDEX_NAME"]);
        AreEqual((short)3, ixName["TYPE"]);
        AreEqual("name", ixName["COLUMN_NAME"]);
        AreEqual("D", ixName["COLLATION"]);                  // DESC key column
    }

    [TestMethod]
    public void SpStatistics_IsUniqueY_ExcludesNonUnique()
    {
        var rows = Run(NewIndexedFixture(), "exec sp_statistics_100 @table_name='cust', @index_name='%', @is_unique='Y'");
        // Summary + clustered PK (2 cols) + unique NC index; the non-unique
        // ix_cust_name is dropped.
        HasCount(4, rows);
        DoesNotContain("ix_cust_name", rows.Select(r => (string?)r["INDEX_NAME"]));
    }

    [TestMethod]
    public void SpStatistics_IndexNameFilter()
    {
        var rows = Run(NewIndexedFixture(), "exec sp_statistics_100 @table_name='cust', @index_name='ix_cust_name'");
        // Summary row plus only the LIKE-matched index's one key column.
        HasCount(2, rows);
        AreEqual("ix_cust_name", rows[1]["INDEX_NAME"]);
    }

    [TestMethod]
    public void SpStatistics_IndexNameLikePrefix()
    {
        var rows = Run(NewIndexedFixture(), "exec sp_statistics_100 @table_name='cust', @index_name='ix%'");
        // Summary row plus only ix_cust_name (ux_cust_region and the PK are
        // excluded by the LIKE prefix).
        HasCount(2, rows);
        AreEqual("ix_cust_name", rows[1]["INDEX_NAME"]);
    }

    [TestMethod]
    public void SpStatistics_ThirteenColumnShape()
    {
        using var reader = NewIndexedFixture().ExecuteReader("exec sp_statistics_100 @table_name='cust'");
        AreEqual(13, reader.FieldCount);
        AreEqual("TABLE_QUALIFIER", reader.GetName(0));
        AreEqual("FILTER_CONDITION", reader.GetName(12));
    }

    // ===== sp_stored_procedures =====

    [TestMethod]
    public void SpStoredProcedures_ListsUserProc()
    {
        var rows = Run(NewIndexedFixture(), "exec sp_stored_procedures @sp_name='myproc'");
        HasCount(1, rows);
        var r = rows[0];
        AreEqual("simulated", r["PROCEDURE_QUALIFIER"]);
        AreEqual("dbo", r["PROCEDURE_OWNER"]);
        AreEqual("myproc;1", r["PROCEDURE_NAME"]);           // trailing ;1 group number
        AreEqual(-1, r["NUM_INPUT_PARAMS"]);
        AreEqual(-1, r["NUM_OUTPUT_PARAMS"]);
        AreEqual(-1, r["NUM_RESULT_SETS"]);
        IsNull(r["REMARKS"]);
        AreEqual((short)2, r["PROCEDURE_TYPE"]);             // SQL_PT_PROCEDURE
    }

    [TestMethod]
    public void SpStoredProcedures_NameLikePattern()
    {
        var rows = Run(NewIndexedFixture(), "exec sp_stored_procedures @sp_name='my%'");
        HasCount(1, rows);
        AreEqual("myproc;1", rows[0]["PROCEDURE_NAME"]);
    }

    /// <summary>
    /// Several matches come back ordered by owner then name, which is the
    /// order real returns them in (probed against SQL Server 2025) — the
    /// ODBC <c>SQLProcedures</c> contract a one-row filter never exercises.
    /// </summary>
    [TestMethod]
    public void SpStoredProcedures_SeveralMatches_OrderedByOwnerThenName()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create schema other",
            "create procedure zproc as select 1",
            "create procedure aproc as select 1",
            "create procedure other.mproc as select 1");
        var rows = Run(sim, "exec sp_stored_procedures @sp_name='%proc'");
        CollectionAssert.AreEqual(
            new[] { "dbo|aproc;1", "dbo|zproc;1", "other|mproc;1" },
            rows.ConvertAll(r => $"{r["PROCEDURE_OWNER"]}|{r["PROCEDURE_NAME"]}"));
    }

    [TestMethod]
    public void SpStoredProcedures_EightColumnShape()
    {
        using var reader = NewIndexedFixture().ExecuteReader("exec sp_stored_procedures @sp_name='myproc'");
        AreEqual(8, reader.FieldCount);
        AreEqual("PROCEDURE_QUALIFIER", reader.GetName(0));
        AreEqual("PROCEDURE_TYPE", reader.GetName(7));
    }
}
