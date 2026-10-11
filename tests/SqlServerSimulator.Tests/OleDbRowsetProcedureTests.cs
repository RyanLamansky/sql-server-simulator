using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The OLE DB schema-rowset procedures SQL Server's OLE DB provider calls on
/// a server it links to, the <c>sp_getschemalock</c> pair it brackets a
/// statement with, and <c>COLLATIONNAME</c>. Every asserted value was probed
/// against SQL Server 2025 (2026-10-11) with the same fixture.
/// </summary>
[TestClass]
public sealed class OleDbRowsetProcedureTests
{
    private static Simulation Fixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type dbo.phone from varchar(20) not null",
            """
            create table dbo.p (id int not null constraint pk_p primary key, code nchar(4) not null constraint uq_p unique);
            create table dbo.t (
                id int identity(1,1) not null constraint pk_t primary key,
                b bit null, ti tinyint not null default 3, de decimal(12,3) null, mo money null,
                da date null, dt2 datetime2(3) null, tm time(0) null,
                vc varchar(50) collate Latin1_General_CS_AS null, vcm varchar(max) null, nvc nvarchar(40) not null default N'x',
                vb varbinary(30) null, g uniqueidentifier null, rv rowversion, x xml null, h hierarchyid null, ph dbo.phone,
                comp as (ti * 2), pid int null constraint fk_t_p references dbo.p(id),
                constraint ck_t check (ti < 200), constraint uq_t unique (nvc, id));
            create index ix_t_vc on dbo.t (vc desc, ti) include (mo);
            create index fx_t on dbo.t (tm) where tm is not null;
            create synonym dbo.syn for dbo.t;
            """,
            "create view dbo.v as select id, nvc from dbo.t",
            "create procedure dbo.pr @a int, @b nvarchar(10) = N'q' output, @c datetime2(4), @d varchar(max), @e dbo.phone as select @a");
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

    private static string Columns(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        return string.Join(" ", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}:{reader.GetDataTypeName(i)}"));
    }

    [TestMethod]
    public void TablesInfo_DescribesTheTableByExactName()
    {
        var sim = Fixture();
        var version = sim.ExecuteScalar("select cast(cast(modify_date as binary(8)) as bigint) from sys.objects where name = 't'");
        AreEqual(
            $"simulated|dbo|t|TABLE|NULL|1|1|21|8|0|{version}|0|NULL|NULL|0",
            Rows(sim, "exec sys.sp_tables_info_90_rowset_64 N't', N'dbo', NULL").Single());
        // The plain bookmark is four bytes, and only _90 has TABLE_FLAGS.
        AreEqual($"simulated|dbo|t|TABLE|NULL|1|1|19|4|0|{version}|0|NULL|NULL", Rows(sim, "exec sys.sp_tables_info_rowset N'T'").Single());
        AreEqual(
            "TABLE_CATALOG:nvarchar TABLE_SCHEMA:nvarchar TABLE_NAME:nvarchar TABLE_TYPE:nvarchar TABLE_GUID:uniqueidentifier BOOKMARKS:bit BOOKMARK_TYPE:int BOOKMARK_DATATYPE:smallint BOOKMARK_MAXIMUM_LENGTH:int BOOKMARK_INFORMATION:int TABLE_VERSION:bigint CARDINALITY:int DESCRIPTION:nvarchar TABLE_PROPID:int TABLE_FLAGS:int",
            Columns(sim, "exec sys.sp_tables_info_90_rowset N't'"));

        // A view and a synonym count no rows; a name is never a pattern, and
        // the type matches whole, case aside.
        AreEqual("VIEW|NULL", string.Join("|", Rows(sim, "exec sys.sp_tables_info_90_rowset_64 N'v'").Single().Split('|')[3], "NULL"));
        AreEqual("SYNONYM", Rows(sim, "exec sys.sp_tables_info_90_rowset_64 N'syn'").Single().Split('|')[3]);
        IsEmpty(Rows(sim, "exec sys.sp_tables_info_90_rowset_64 N't%'"));
        IsEmpty(Rows(sim, "exec sys.sp_tables_info_90_rowset_64 N't', NULL, N'TABLE,VIEW'"));
        HasCount(1, Rows(sim, "exec sys.sp_tables_info_90_rowset_64 N't', NULL, N'table'"));
        _ = sim.AssertSqlError("exec sys.sp_tables_info_90_rowset_64", 201);

        // The "2" form lists the schema's objects by type and name.
        AreEqual("p|t|v", string.Join("|", Rows(sim, "exec sys.sp_tables_info_rowset2 N'dbo', NULL").Where(row => !row.Contains("|SYNONYM|", StringComparison.Ordinal)).Select(row => row.Split('|')[2])));
    }

    [TestMethod]
    public void Columns100_ReportsEachTypeNatively()
    {
        var rows = Rows(Fixture(), "exec sys.sp_columns_100_rowset N't', N'dbo', NULL")
            .ConvertAll(row => row.Split('|'))
            .ToDictionary(cells => cells[3], cells => (Flags: cells[9], Type: cells[11], Lengths: $"{cells[13]}/{cells[14]}/{cells[15]}/{cells[16]}/{cells[17]}", Collation: $"{cells[20]}/{cells[23]}/{cells[28]}/{cells[29]}/{cells[30]}/{cells[31]}", Domain: cells[26], Udt: cells[38]));
        AreEqual(("16", "3", "NULL/NULL/10/NULL/NULL"), (rows["id"].Flags, rows["id"].Type, rows["id"].Lengths));
        AreEqual(("20", "17"), (rows["ti"].Flags, rows["ti"].Type));
        AreEqual(("116", "131", "NULL/NULL/12/3/NULL"), (rows["de"].Flags, rows["de"].Type, rows["de"].Lengths));
        AreEqual(("116", "6", "NULL/NULL/19/NULL/NULL"), (rows["mo"].Flags, rows["mo"].Type, rows["mo"].Lengths));
        AreEqual(("116", "133", "NULL/NULL/NULL/NULL/0"), (rows["da"].Flags, rows["da"].Type, rows["da"].Lengths));
        AreEqual(("1073741940", "135", "NULL/NULL/NULL/NULL/3"), (rows["dt2"].Flags, rows["dt2"].Type, rows["dt2"].Lengths));
        AreEqual(("1073741940", "145"), (rows["tm"].Flags, rows["tm"].Type));
        AreEqual(("100", "129", "50/50/NULL/NULL/NULL", "iso_1/Latin1_General_CS_AS/1033/196608/52/0x0904C00000"),
            (rows["vc"].Flags, rows["vc"].Type, rows["vc"].Lengths, rows["vc"].Collation));
        AreEqual(("230", "129", "0/0/NULL/NULL/NULL"), (rows["vcm"].Flags, rows["vcm"].Type, rows["vcm"].Lengths));
        AreEqual(("4", "130", "40/80/NULL/NULL/NULL", "iso_1/SQL_Latin1_General_CP1_CI_AS/1033/196609/52/0x0904D00034"),
            (rows["nvc"].Flags, rows["nvc"].Type, rows["nvc"].Lengths, rows["nvc"].Collation));
        AreEqual(("116", "72"), (rows["g"].Flags, rows["g"].Type));
        AreEqual(("528", "128", "8/8/NULL/NULL/NULL"), (rows["rv"].Flags, rows["rv"].Type, rows["rv"].Lengths));
        AreEqual(("230", "141", "NULL/NULL/NULL/NULL/NULL"), (rows["x"].Flags, rows["x"].Type, rows["x"].Lengths));
        AreEqual(("100", "132", "892/892/NULL/NULL/NULL", "hierarchyid"), (rows["h"].Flags, rows["h"].Type, rows["h"].Lengths, rows["h"].Udt));
        AreEqual(("4", "129", "phone"), (rows["ph"].Flags, rows["ph"].Type, rows["ph"].Domain));
        // A non-character column carries the database's sort id alone.
        AreEqual("NULL/NULL/NULL/NULL/52/NULL", rows["b"].Collation);
    }

    [TestMethod]
    public void Columns90AndPlain_ReportTheNewerTypesDownLevel()
    {
        var sim = Fixture();
        string Describe(string procedure, string column)
        {
            var cells = Rows(sim, $"exec sys.{procedure} N't', NULL, N'{column}'").Single().Split('|');
            return $"{cells[9]}/{cells[11]}/{cells[13]}/{cells[14]}/{cells[23]}";
        }
        // A date or time type reads as the nvarchar of its text, in the
        // database's collation.
        AreEqual("100/130/23/46/SQL_Latin1_General_CP1_CI_AS", Describe("sp_columns_90_rowset", "dt2"));
        AreEqual("100/130/10/20/SQL_Latin1_General_CP1_CI_AS", Describe("sp_columns_rowset", "da"));
        AreEqual("100/130/8/16/SQL_Latin1_General_CP1_CI_AS", Describe("sp_columns_rowset", "tm"));
        // _90 keeps the MAX types and xml; the plain version reports them as
        // their legacy LOBs.
        AreEqual("230/129/0/0/SQL_Latin1_General_CP1_CI_AS", Describe("sp_columns_90_rowset", "vcm"));
        AreEqual("230/129/2147483647/2147483647/SQL_Latin1_General_CP1_CI_AS", Describe("sp_columns_rowset", "vcm"));
        AreEqual("230/141/NULL/NULL/NULL", Describe("sp_columns_90_rowset", "x"));
        AreEqual("230/130/1073741823/2147483646/SQL_Latin1_General_CP1_CI_AS", Describe("sp_columns_rowset", "x"));
        AreEqual("100/128/892/892/NULL", Describe("sp_columns_rowset", "h"));
        AreEqual(33, sim.ExecuteReader("exec sys.sp_columns_rowset N't'").FieldCount);
        AreEqual(40, sim.ExecuteReader("exec sys.sp_columns_90_rowset N't'").FieldCount);
        AreEqual(42, sim.ExecuteReader("exec sys.sp_columns_100_rowset N't'").FieldCount);
    }

    [TestMethod]
    public void Indexes_ListKeyColumnsUniqueLast()
    {
        var sim = Fixture();
        _ = sim.ExecuteScalar("insert dbo.p values (1, N'a'), (2, N'b')");
        AreEqual(
            string.Join(Environment.NewLine,
                "fx_t|0|0|0|1|tm|1|NULL|0|([tm] IS NOT NULL)|0",
                "ix_t_vc|0|0|0|1|vc|2|NULL|0|NULL|0",
                "ix_t_vc|0|0|0|2|ti|1|NULL|0|NULL|0",
                "pk_t|1|1|1|1|id|1|0|0|NULL|1",
                "uq_t|0|1|0|1|nvc|1|0|0|NULL|0",
                "uq_t|0|1|0|2|id|1|0|0|NULL|0"),
            string.Join(Environment.NewLine, Rows(sim, "exec sys.sp_indexes_100_rowset N't', NULL, N'dbo'")
                .Select(row => row.Split('|'))
                .Select(cells => string.Join("|", cells[5], cells[6], cells[7], cells[8], cells[16], cells[17], cells[20], cells[21], cells[22], cells[23], cells[24]))));
        // A unique index's cardinality is the table's row count.
        AreEqual("pk_p|2|1", string.Join("|", Rows(sim, "exec sys.sp_indexes_100_rowset N'p', N'pk_p'").Single().Split('|') is var p ? [p[5], p[21], p[22]] : []));
        AreEqual(26, sim.ExecuteReader("exec sys.sp_indexes_90_rowset N't'").FieldCount);
        AreEqual(25, sim.ExecuteReader("exec sys.sp_indexes_rowset N't'").FieldCount);
    }

    [TestMethod]
    public void KeysConstraintsAndPrivileges()
    {
        var sim = Fixture();
        AreEqual("simulated|dbo|t|simulated|dbo|ck_t|([ti]<(200))|NULL", Rows(sim, "exec sys.sp_check_constbytable_rowset N't', N'dbo', NULL, NULL").Single());
        IsEmpty(Rows(sim, "exec sys.sp_check_constbytable_rowset N'p'"));
        AreEqual("simulated|dbo|t|id|NULL|NULL|1|pk_t", Rows(sim, "exec sys.sp_primary_keys_rowset N't', N'dbo'").Single());
        const string fk = "simulated|dbo|p|id|NULL|NULL|simulated|dbo|t|pid|NULL|NULL|1|NO ACTION|NO ACTION|pk_p|fk_t_p|3";
        AreEqual(fk, Rows(sim, "exec sys.sp_foreign_keys_rowset N'p', N'dbo', NULL, NULL, NULL").Single());
        AreEqual(fk, Rows(sim, "exec sys.sp_foreign_keys_rowset2 N't', N'dbo', NULL, NULL, NULL").Single());
        AreEqual(fk, Rows(sim, "exec sys.sp_foreign_keys_rowset3 N'dbo', NULL, N'dbo', NULL").Single());
        // The plain form starts from the referenced table, which it requires.
        IsEmpty(Rows(sim, "exec sys.sp_foreign_keys_rowset NULL, NULL, N't', N'dbo', NULL"));
        AreEqual("simulated|NULL", Rows(sim, "exec sys.sp_catalogs_rowset N'SIMULATED'").Single());
        AreEqual("master|model|msdb|simulated|tempdb", string.Join("|", Rows(sim, "exec sys.sp_catalogs_rowset2").Select(row => row.Split('|')[0])));
        AreEqual(
            "dbo|dbo|simulated|dbo|p|DELETE|1,dbo|dbo|simulated|dbo|p|INSERT|1,dbo|dbo|simulated|dbo|p|REFERENCES|1,dbo|dbo|simulated|dbo|p|SELECT|1,dbo|dbo|simulated|dbo|p|UPDATE|1",
            string.Join(",", Rows(sim, "exec sys.sp_table_privileges_rowset N'p', N'dbo', NULL, NULL")));
        AreEqual(
            "dbo|dbo|simulated|dbo|p|code|NULL|NULL|INSERT|1,dbo|dbo|simulated|dbo|p|code|NULL|NULL|REFERENCES|1,dbo|dbo|simulated|dbo|p|code|NULL|NULL|SELECT|1,dbo|dbo|simulated|dbo|p|code|NULL|NULL|UPDATE|1",
            string.Join(",", Rows(sim, "exec sys.sp_column_privileges_rowset N'p', NULL, N'code', NULL, NULL")));
    }

    [TestMethod]
    public void TableStatistics2_SendsAResultSetPerStatistic()
    {
        var sim = Fixture();
        _ = sim.ExecuteScalar("insert dbo.p values (1, N'a'), (2, N'b'); update statistics dbo.p");
        var completed = new List<int>();
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        connection.InfoMessage += (_, e) => completed.AddRange(e.Errors.Cast<SimulatedError>().Select(error => error.Number));
        using var command = connection.CreateCommand("exec sys.sp_table_statistics2_rowset N'p', N'dbo', N'simulated', NULL, NULL, NULL");
        using var reader = command.ExecuteReader();
        var sets = new List<string>();
        do
        {
            while (reader.Read())
                sets.Add($"{reader["STATISTICS_NAME"]}|{reader["STATISTICS_TYPE"]}|{reader["COLUMN_NAME"]}|{reader["ORDINAL_POSITION"]}|{reader["SAMPLE_PCT"]}|{reader["NO_OF_RANGES"]}|{reader["TUPLE_CARDINALITY"]}|{reader["TABLE_CARDINALITY"]}|{((float)reader["AVG_COLUMN_LENGTH"]).ToString(CultureInfo.InvariantCulture)}");
        }
        while (reader.NextResult());
        AreEqual("pk_p|5|id|1|100|2|2|2|4,uq_p|5|code|1|100|2|2|2|8,uq_p|5|id|2|100|2|2|2|4", string.Join(",", sets));
        AreEqual("2528,2528", string.Join(",", completed));

        // A view has no statistics to send, a name nothing has is real's
        // replication message, and the old procedure's set is always empty.
        HasCount(1, ResultSets(sim, "exec sys.sp_table_statistics2_rowset N'v'"));
        _ = sim.AssertSqlError("exec sys.sp_table_statistics2_rowset N'nope'", 20001);
        IsEmpty(Rows(sim, "exec sys.sp_table_statistics_rowset N't'"));
    }

    [TestMethod]
    public void ProviderTypes_ListTheGenerationsTypes()
    {
        var sim = Fixture();
        HasCount(30, Rows(sim, "exec sys.sp_provider_types_100_rowset NULL, NULL"));
        AreEqual("datetime2|135|27|'|'|scale|1|0|4|NULL|0|0|datetime2|0|7|NULL|NULL|NULL|0|1|1",
            Rows(sim, "exec sys.sp_provider_types_100_rowset 135, 1").Single());
        AreEqual("datetime2|130|27", string.Join("|", Rows(sim, "exec sys.sp_provider_types_90_rowset NULL, NULL").Single(row => row.StartsWith("datetime2|", StringComparison.Ordinal)).Split('|')[..3]));
        AreEqual("ntext,nchar", string.Join(",", Rows(sim, "exec sys.sp_provider_types_100_rowset 130, 0").Select(row => row.Split('|')[0])));
    }

    [TestMethod]
    public void ProceduresAndParameters()
    {
        var sim = Fixture();
        var procedure = Rows(sim, "exec sys.sp_procedures_rowset N'pr', NULL, NULL").Single().Split('|');
        AreEqual("simulated|dbo|pr;1|3|NULL|NULL", string.Join("|", procedure[..6]));
        IsEmpty(Rows(sim, "exec sys.sp_procedures_rowset N'pr', 2, NULL"));
        string Parameters(string procedureName) => string.Join(",", Rows(sim, $"exec sys.{procedureName} N'pr'")
            .Select(row => row.Split('|')).Select(cells => $"{cells[3]}:{cells[5]}:{cells[9]}:{cells[10]}:{cells[15]}"));
        AreEqual("@RETURN_VALUE:4:3:NULL:int,@a:1:3:NULL:int,@b:2:130:10:nvarchar,@c:1:135:NULL:datetime2,@d:1:129:0:varchar,@e:1:129:20:NULL",
            Parameters("sp_procedure_params_100_rowset"));
        AreEqual("@RETURN_VALUE:4:3:NULL:int,@a:1:3:NULL:int,@b:2:130:10:nvarchar,@c:1:130:24:datetime2,@d:1:129:2147483647:text,@e:1:129:20:varchar",
            Parameters("sp_procedure_params_rowset"));
        AreEqual("4", Rows(sim, "exec sys.sp_procedure_params_100_rowset N'pr', 1, NULL, N'@c'").Single().Split('|')[26]);
    }

    [TestMethod]
    public void SchemaLock_HoldsTheObjectForTheTransaction()
    {
        var sim = Fixture();
        var error = sim.AssertSqlError("declare @h int = 1, @v bigint; exec sys.sp_getschemalock @h output, @v output, N'\"simulated\".\"dbo\".\"t\"'", 3918);
        AreEqual("sys.sp_getschemalock", error.Errors[0].Procedure);
        AreEqual(3918, sim.ExecuteScalar("declare @h int = 1, @v bigint, @r int; begin try exec @r = sys.sp_getschemalock @h output, @v output, N't' end try begin catch end catch; select @r"));

        var version = sim.ExecuteScalar("select cast(cast(modify_date as binary(8)) as bigint) from sys.objects where name = 't'");
        AreEqual($"1|{version}|1|2|0|3", sim.ExecuteScalar("""
            begin tran;
            declare @h int = 1, @v bigint, @h2 int, @v2 bigint, @h3 int, @r int;
            exec sys.sp_getschemalock @h output, @v output, N'"simulated"."dbo"."t"';
            exec sys.sp_getschemalock @h2 output, @v2 output, N't';
            exec sys.sp_getschemalock @h3 output, @v2 output, N'v';
            exec @r = sys.sp_releaseschemalock 1;
            exec sys.sp_getschemalock @h output, @v2 output, N't';
            select concat(@h2, '|', @v, '|', 1, '|', @h3, '|', @r, '|', @h);
            rollback
            """));
        AreEqual(1, sim.ExecuteScalar("""
            begin tran;
            declare @h int = 1, @v bigint;
            exec sys.sp_getschemalock @h output, @v output, N't';
            select count(*) from sys.dm_tran_locks where request_session_id = @@spid and resource_type = 'OBJECT' and request_mode = 'Sch-S' and resource_associated_entity_id = object_id('t');
            rollback
            """));

        // A missing object and an unknown handle each end the batch and roll
        // the transaction back.
        using var connection = sim.CreateOpenConnection();
        var missing = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; declare @h int = 1, @v bigint; exec sys.sp_getschemalock @h output, @v output, N'nope'; select 1").ExecuteScalar());
        AreEqual((208, (byte)132, "Invalid object name 'nope'."), (missing.Number, missing.State, missing.Errors[0].Message));
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        var unknown = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; exec sys.sp_releaseschemalock 77; select 1").ExecuteScalar());
        AreEqual("Schema lock with handle 77 not found.", unknown.Errors[0].Message);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void CollationName_NamesTheCollationOfTdsBytes()
    {
        var sim = new Simulation();
        AreEqual("SQL_Latin1_General_CP1_CI_AS|Latin1_General_CS_AS|Latin1_General_CI_AS|Japanese_CI_AS_KS|Latin1_General_BIN2|Albanian_100_CI_AI|Japanese_XJIS_140_CI_AI_VSS",
            sim.ExecuteScalar("""
                select concat_ws('|', collationname(0x0904D00034), collationname(0x0904C00000), collationname(0x0904D00000),
                    collationname(cast(collationproperty('Japanese_CI_AS_KS', 'TDSCollation') as binary(5))), collationname(0x0904000200),
                    collationname(cast(collationproperty('Albanian_100_CI_AI_SC', 'TDSCollation') as binary(5))),
                    collationname(cast(collationproperty('Japanese_XJIS_140_CI_AI', 'TDSCollation') as binary(5))))
                """));
        AreEqual("nvarchar", sim.ExecuteReader("select collationname(0x0904D00034)").GetDataTypeName(0));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select collationname(0x0904D0003400)"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select collationname(0x0904D00034, 0)"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select collationname(null)"));
        _ = sim.AssertSqlError("select collationname('abc')", 257);
        _ = sim.AssertSqlError("select collationname()", 189);
    }
}
