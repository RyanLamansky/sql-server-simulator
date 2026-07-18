using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the catalog-view predicate pushdown (<c>Selection.BuildSqlProjection</c>
/// → <c>Selection.ForCatalogView</c>): a leftmost pushdown-aware catalog view
/// (<c>sys.columns</c> and peers) with a top-level <c>&lt;key&gt; = &lt;row-independent
/// value&gt;</c> WHERE conjunct must hand the key into the row generator so it
/// enumerates only the matching object (the <c>Seek(view.column)</c> trace), a
/// NULL comparand must short-circuit to no rows (<c>SeekEmpty</c>), and shapes
/// that can't push (OR-combined predicate, catalog view not leftmost, no
/// eligible conjunct) must run the full generator (<c>Scan</c>). The pushdown is
/// result-transparent — the full WHERE re-applies as a residual filter — so the
/// correctness suite passes either way; these read the opt-in
/// <see cref="CatalogPushdownDiagnostics"/> trace to assert the path directly and
/// confirm the rows stay correct under it.
/// </summary>
[TestClass]
public sealed class CatalogPushdownTests
{
    public TestContext TestContext { get; set; } = null!;

    // Runs `setup` then `query` on one connection, capturing the pushdown trace
    // and each result row's first column.
    private static (List<string> Trace, List<object?> Rows) Run(string query, string? setup = null)
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using (var s = connection.CreateCommand())
        {
            s.CommandText = setup ?? """
                create table t1 (a int not null primary key, b nvarchar(20) null);
                create table t2 (c int not null primary key, d date null, e money null);
                create table t3 (f bigint not null primary key);
                """;
            _ = s.ExecuteNonQuery();
        }

        CatalogPushdownDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            var rows = new List<object?>();
            while (reader.Read())
                rows.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
            return (CatalogPushdownDiagnostics.Sink, rows);
        }
        finally
        {
            CatalogPushdownDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void ColumnsByObjectIdFunction_Seeks()
    {
        var (trace, rows) = Run("select name from sys.columns c where c.object_id = object_id('dbo.t2') order by column_id");
        Contains("Seek(columns.object_id)", trace);
        DoesNotContain("Scan(columns)", trace);
        HasCount(3, rows);
        AreEqual("c", rows[0]);
        AreEqual("d", rows[1]);
        AreEqual("e", rows[2]);
    }

    [TestMethod]
    public void AllColumnsByObjectId_Seeks()
    {
        var (trace, rows) = Run("select name from sys.all_columns where object_id = object_id('dbo.t2') order by column_id");
        Contains("Seek(all_columns.object_id)", trace);
        HasCount(3, rows);
    }

    [TestMethod]
    public void ColumnsByLiteralObjectId_Seeks()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using (var s = connection.CreateCommand())
        {
            s.CommandText = "create table t2 (c int not null primary key, d date null, e money null)";
            _ = s.ExecuteNonQuery();
        }
        int id;
        using (var idc = connection.CreateCommand())
        {
            idc.CommandText = "select object_id('dbo.t2')";
            id = (int)idc.ExecuteScalar()!;
        }

        CatalogPushdownDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"select count(*) from sys.columns where object_id = {id}";
            using var reader = command.ExecuteReader();
            _ = reader.Read();
            AreEqual(3, reader.GetInt32(0));
            Contains("Seek(columns.object_id)", CatalogPushdownDiagnostics.Sink!);
        }
        finally
        {
            CatalogPushdownDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void ColumnsByVariable_Seeks()
    {
        var (trace, rows) = Run("""
            declare @id int = object_id('dbo.t3');
            select name from sys.columns where object_id = @id order by column_id
            """);
        Contains("Seek(columns.object_id)", trace);
        HasCount(1, rows);
        AreEqual("f", rows[0]);
    }

    [TestMethod]
    public void ColumnsPredicateAndedWithOther_Seeks()
    {
        var (trace, rows) = Run("select name from sys.columns c where c.object_id = object_id('dbo.t2') and c.is_nullable = 1 order by column_id");
        Contains("Seek(columns.object_id)", trace);
        // Residual WHERE still drops the non-nullable key column.
        HasCount(2, rows);
        AreEqual("d", rows[0]);
        AreEqual("e", rows[1]);
    }

    [TestMethod]
    public void ColumnsOrPredicate_DoesNotPush()
    {
        var (trace, rows) = Run("select name from sys.columns c where c.object_id = object_id('dbo.t2') or c.object_id = object_id('dbo.t3') order by object_id, column_id");
        Contains("Scan(columns)", trace);
        DoesNotContain("Seek(columns.object_id)", trace);
        // Union of t2 (c, d, e) and t3 (f) columns.
        HasCount(4, rows);
    }

    [TestMethod]
    public void CatalogViewNotLeftmost_DoesNotPush()
    {
        // Leftmost source is the base table t2; sys.columns is the join's right
        // side, so no pushdown fires — but results stay correct.
        var (trace, rows) = Run("""
            select c.name from t2 join sys.columns c on c.object_id = object_id('dbo.t2')
            where t2.c is not null order by c.column_id
            """);
        DoesNotContain("Seek(columns.object_id)", trace);
        // t2 has no rows, so the inner join yields nothing regardless.
        IsEmpty(rows);
    }

    [TestMethod]
    public void UnknownObject_SeeksEmpty()
    {
        var (trace, rows) = Run("select name from sys.columns where object_id = object_id('dbo.does_not_exist')");
        Contains("SeekEmpty(columns.object_id)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void ExplicitNullComparand_SeeksEmpty()
    {
        var (trace, rows) = Run("select name from sys.columns where object_id = null");
        Contains("SeekEmpty(columns.object_id)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void IndexesByObjectId_Seeks()
    {
        var (trace, rows) = Run("select name from sys.indexes where object_id = object_id('dbo.t1')");
        Contains("Seek(indexes.object_id)", trace);
        // t1's clustered PK index.
        HasCount(1, rows);
    }

    [TestMethod]
    public void IndexColumnsByObjectId_Seeks()
    {
        var (trace, rows) = Run("select index_id from sys.index_columns where object_id = object_id('dbo.t1')");
        Contains("Seek(index_columns.object_id)", trace);
        HasCount(1, rows);
    }

    [TestMethod]
    public void ParametersByObjectId_Seeks()
    {
        var (trace, rows) = Run(
            "select name from sys.parameters where object_id = object_id('dbo.p') order by parameter_id",
            setup: "create procedure p @x int, @y nvarchar(10) as select 1");
        Contains("Seek(parameters.object_id)", trace);
        HasCount(2, rows);
        AreEqual("@x", rows[0]);
        AreEqual("@y", rows[1]);
    }

    [TestMethod]
    public void ExtendedPropertiesByMajorId_Seeks()
    {
        var (trace, rows) = Run(
            "select cast(value as nvarchar(100)) from sys.extended_properties where major_id = object_id('dbo.t1')",
            setup: """
                create table t1 (a int);
                create table t2 (b int);
                exec sp_addextendedproperty @name = N'MS_Description', @value = N'one', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N't1';
                exec sp_addextendedproperty @name = N'MS_Description', @value = N'two', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N't2';
                """);
        Contains("Seek(extended_properties.major_id)", trace);
        HasCount(1, rows);
        AreEqual("one", rows[0]);
    }

    [TestMethod]
    public void NoEligibleConjunct_Scans()
    {
        var (trace, _) = Run("select name from sys.columns where name = 'a'");
        Contains("Scan(columns)", trace);
        DoesNotContain("Seek(columns.object_id)", trace);
    }
}
