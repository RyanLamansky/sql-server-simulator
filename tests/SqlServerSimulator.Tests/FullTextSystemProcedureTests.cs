using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The full-text system procedures — the deprecated <c>sp_help_fulltext_*</c>
/// reports, <c>sp_fulltext_database</c> and <c>sp_fulltext_load_thesaurus_file</c> —
/// and <c>sys.fulltext_document_types</c>, against answers probed from SQL
/// Server 2025 (2026-10-06).
/// </summary>
[TestClass]
public sealed class FullTextSystemProcedureTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create fulltext catalog ftc as default",
            "create fulltext catalog c2",
            "create table dbo.t (id int not null constraint pk_t primary key, title nvarchar(200), body nvarchar(max), doc varbinary(max), ext char(4))",
            "create fulltext index on dbo.t (title, body language 1036, doc type column ext language 0) key index pk_t on ftc",
            "create table dbo.nf (id int not null primary key, x int)",
            "create view dbo.v as select id from dbo.t");
        return sim;
    }

    private static List<object?[]> Rows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        List<object?[]> rows = [];
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    [TestMethod]
    public void Catalogs_Report_Each_Catalog_With_Its_Table_Count()
    {
        var sim = Seeded();
        var rows = Rows(sim, "exec sp_help_fulltext_catalogs");
        HasCount(2, rows);
        CollectionAssert.AreEqual(new object?[] { 5, "ftc", null, 0, 1 }, rows[0]);
        CollectionAssert.AreEqual(new object?[] { 6, "c2", null, 0, 0 }, rows[1]);
        HasCount(1, Rows(sim, "exec sp_help_fulltext_catalogs 'FTC'"));

        var missing = sim.AssertSqlError("exec sp_help_fulltext_catalogs 'nope'", 7641);
        AreEqual("Full-Text catalog 'nope' does not exist in database 's' or user does not have permission to perform this action.", missing.Message);
        AreEqual(22, missing.Errors[0].LineNumber);
        AreEqual("sp_help_fulltext_catalogs", missing.Errors[0].Procedure);
    }

    [TestMethod]
    public void Tables_And_Columns_Report_The_Index()
    {
        var sim = Seeded();
        var tables = Rows(sim, "exec sp_help_fulltext_tables");
        HasCount(1, tables);
        CollectionAssert.AreEqual(new object?[] { "dbo", "t", "pk_t", 1, true, "ftc" }, tables[0]);
        IsEmpty(Rows(sim, "exec sp_help_fulltext_tables 'c2'"));

        var columns = Rows(sim, "exec sp_help_fulltext_columns 'dbo.t'");
        HasCount(3, columns);
        CollectionAssert.AreEqual(new object?[] { "dbo", "t", "body", 3, null, null, 1036 }, new[] { columns[1][0], columns[1][2], columns[1][3], columns[1][4], columns[1][5], columns[1][6], columns[1][7] });
        CollectionAssert.AreEqual(new object?[] { "doc", 4, "ext", 5, 0 }, new[] { columns[2][3], columns[2][4], columns[2][5], columns[2][6], columns[2][7] });
        HasCount(1, Rows(sim, "exec sp_help_fulltext_columns @table_name = 't', @column_name = 'doc'"));
        IsEmpty(Rows(sim, "exec sp_help_fulltext_columns 'nf', 'x'"));
    }

    [TestMethod]
    [DataRow("exec sp_help_fulltext_tables null, 'nope'", 15009, 38)]
    [DataRow("exec sp_help_fulltext_columns 'nope'", 15009, 22)]
    [DataRow("exec sp_help_fulltext_tables null, 'v'", 15218, 45)]
    [DataRow("exec sp_help_fulltext_columns 'v'", 15218, 29)]
    [DataRow("exec sp_help_fulltext_columns 't', 'nope'", 15104, 40)]
    [DataRow("exec sp_help_fulltext_tables 'nope'", 7641, 25)]
    [DataRow("exec sp_help_fulltext_system_components", 15600, 9)]
    [DataRow("exec sp_help_fulltext_system_components 'all', 1036", 15600, 26)]
    public void Help_Procedures_Refuse_What_Real_Refuses(string sql, int number, int line)
    {
        var error = Seeded().AssertSqlError(sql, number);
        AreEqual(line, error.Errors[0].LineNumber);
    }

    [TestMethod]
    public void System_Components_List_Word_Breakers_And_Filters()
    {
        var sim = Seeded();
        HasCount(169, Rows(sim, "exec sp_help_fulltext_system_components 'all'"));
        var breaker = Rows(sim, "exec sp_help_fulltext_system_components 'wordbreaker', 1036");
        HasCount(1, breaker);
        CollectionAssert.AreEqual(new object?[] { "wordbreaker", "1036", new Guid("97c2e40b-f712-4084-8dfb-1806c8a87037"), "MSWB7.dll", "16.0.5306.1000", "Microsoft Corporation" }, breaker[0]);
        IsEmpty(Rows(sim, "exec sp_help_fulltext_system_components 'protocol handler'"));
        AreEqual(110, sim.ExecuteScalar<int>("select count(*) from sys.fulltext_document_types"));
        AreEqual("Query.dll", sim.ExecuteScalar("select path from sys.fulltext_document_types where document_type = '.txt'"));
    }

    [TestMethod]
    public void Disabling_Full_Text_Search_Refuses_The_Help_Procedures()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("exec sp_fulltext_database 'disable'");
        AreEqual(0, sim.ExecuteScalar<int>("select cast(is_fulltext_enabled as int) from sys.databases where name = db_name()"));
        AreEqual(7, sim.AssertSqlError("exec sp_help_fulltext_catalogs", 15601).Errors[0].LineNumber);
        // Searches carry on regardless.
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from dbo.t where contains(title, 'x')"));
        _ = sim.ExecuteNonQuery("exec sp_fulltext_database 'Enable '");
        AreEqual(1, sim.ExecuteScalar<int>("select cast(is_fulltext_enabled as int) from sys.databases where name = db_name()"));

        AreEqual(40, sim.AssertSqlError("exec sp_fulltext_database 'bogus'", 15600).Errors[0].LineNumber);
        _ = sim.AssertSqlError("begin tran; exec sp_fulltext_database 'enable'", 15002);
        _ = sim.AssertSqlError("use master; exec sp_fulltext_database 'enable'", 9966);
    }

    [TestMethod]
    public void Loading_A_Thesaurus_Succeeds_For_A_Language_That_Ships_One()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar<int>("declare @r int; exec @r = sys.sp_fulltext_load_thesaurus_file 1033; select @r"));
        var error = sim.AssertSqlError("exec sys.sp_fulltext_load_thesaurus_file 1035", 50000);
        AreEqual("Error 30050, Level 16, State 1, Procedure sys.sp_fulltext_load_thesaurus_file, Line 41, Message: Both the thesaurus file for lcid '1035' and the global thesaurus could not be loaded.", error.Message);
        AreEqual("sys.sp_fulltext_rethrow_error", error.Errors[0].Procedure);
        AreEqual(36, error.Errors[0].LineNumber);
        Contains("lcid '(null)'", sim.AssertSqlError("exec sys.sp_fulltext_load_thesaurus_file null", 50000).Message);
        _ = sim.AssertSqlError("begin tran; exec sys.sp_fulltext_load_thesaurus_file 1033", 15002);
    }
}
