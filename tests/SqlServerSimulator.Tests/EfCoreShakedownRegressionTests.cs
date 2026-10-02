using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Shapes EF Core's own SQL Server functional-test suite sends that the
/// simulator answered differently from SQL Server 2025, each pinned to real's
/// answer (probed 2026-10-02).
/// </summary>
[TestClass]
public sealed class EfCoreShakedownRegressionTests
{
    /// <summary>
    /// A parenthesized boolean group whose first operand is a subquery with a
    /// comma of its own — EF's null-semantics expansion of a subquery equality.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 where 1 = 2 or ((select top(1) 1 from sys.objects order by 1, 1) is null and (select 1) is null)", null)]
    [DataRow("select 1 where 1 = 2 or ((select top(1) 1 from sys.objects order by 1, 1) is not null and (select 1, 2 where 0 = 1) is null)", 116)]
    public void BooleanGroup_LedBySubqueryWithComma_Parses(string query, int? error)
    {
        var simulation = new Simulation();
        if (error is int number)
            _ = simulation.AssertSqlError(query, number);
        else
            IsNull(simulation.ExecuteScalar(query));
    }

    /// <summary>
    /// <c>OFFSET</c> / <c>FETCH</c> may read an enclosing query's columns, as
    /// <c>TOP</c> may — EF's <c>ElementAt(column)</c> — while the query's own
    /// columns stay Msg 4115.
    /// </summary>
    [TestMethod]
    public void OffsetFetch_ReadOuterColumns()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int primary key, c nvarchar(10)); insert a values (1, null), (2, N'x'), (3, N'y')");

        AreEqual("1:,2:x,3:y", simulation.ExecuteScalar(
            "select string_agg(concat(id, ':', c), ',') within group (order by id) from (select o.id, (select b.c from a b order by b.id offset o.id - 1 rows fetch next 1 rows only) c from a o) q"));
        AreEqual("1,2,3", simulation.ExecuteScalar(
            "select string_agg(n, ',') within group (order by id) from (select o.id, (select count(*) from (select b.id from a b order by b.id offset 0 rows fetch next o.id rows only) f) n from a o) q"));
        _ = simulation.AssertSqlError("select b.c from a b order by b.id offset b.id rows", 4115);
        _ = simulation.AssertSqlError("select (select b.c from a b order by b.id offset o.c rows) from a o", 10743);
    }

    /// <summary>
    /// A MERGE's rows may reference each other — or themselves — through a key
    /// on the table itself, as an INSERT's may: the key is checked once the
    /// rows are written.
    /// </summary>
    [TestMethod]
    public void Merge_SelfReferencingKey_CheckedAfterTheWrite()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table l (id int primary key, r int null references l (id))");
        _ = simulation.ExecuteNonQuery("merge l using (values (1, 2), (2, 1), (3, 3)) as i (id, r) on 1 = 0 when not matched then insert (id, r) values (i.id, i.r);");
        AreEqual(3, simulation.ExecuteScalar("select count(*) from l"));

        _ = simulation.AssertSqlError("merge l using (values (4, 99)) as i (id, r) on 1 = 0 when not matched then insert (id, r) values (i.id, i.r);", 547);
        AreEqual(3, simulation.ExecuteScalar("select count(*) from l"));
    }

    /// <summary>
    /// A prefix probe against a key whose later column is NULL — a uniqueness
    /// check had widened the seek cache's entry for the leading column to the
    /// whole key, which filed no row with a NULL in it, so <c>WHERE r = 1</c>
    /// missed those rows.
    /// </summary>
    [TestMethod]
    [DataRow("create unique index ix on p (r, d)")]
    [DataRow("create unique index ix on p (r, d) where d is not null")]
    [DataRow("create index ix on p (r, d)")]
    public void PrefixSeek_FindsRowsWithNullLaterKeyColumns(string index)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table p (id int primary key, d int null, r int not null); " + index);
        _ = simulation.ExecuteNonQuery("insert p values (1, 1, 1), (2, null, 1), (3, 3, 2)");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from p where r = 1"));
        _ = simulation.ExecuteNonQuery("update p set d = null where id = 3");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from p where r = 2"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from p where r = 1 and d is null"));
        _ = simulation.ExecuteNonQuery("update p set d = 5 where id = 2");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from p where r = 1 and d = 5"));
    }

    /// <summary>
    /// Every non-comparable column of a DISTINCT projection reports its own
    /// Msg 421, in select-list order.
    /// </summary>
    [TestMethod]
    public void Distinct_ReportsEachNonComparableColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table j (id int, a json, x xml, b json)");
        var error = simulation.AssertSqlError("select distinct id, a, x, b from j", 421);
        AreEqual("json,xml,json", string.Join(",", error.Errors.Cast<SimulatedError>().Select(static e => e.Message.Split(' ')[1])));
    }

    /// <summary>
    /// A binary needle searches bytes — EF's <c>byte[].Contains</c> — with a
    /// <c>bigint</c> result over a <c>varbinary(max)</c> haystack.
    /// </summary>
    [TestMethod]
    public void CharIndex_SearchesBinaryHaystacks()
    {
        AreEqual("2,0,2,,2,0,2,1", new Simulation().ExecuteScalar(
            "select concat_ws(',', charindex(0x02, 0x010203), charindex(0x0203, 0x01020302, 3), charindex(cast(0x01 as varbinary(max)), 0x0001), isnull(str(charindex(0x01, cast(null as varbinary(10)))), ''), charindex(0x01, 0x0001, -5), charindex(0x, 0x01), charindex(cast(0x02 as binary(2)), 0x01020000), charindex(0x0100, cast(0x01 as binary(3))))"));
        AreEqual("bigint", new Simulation().ExecuteScalar(
            "declare @b varbinary(max) = 0x01; select sql_variant_property(charindex(0x01, @b), 'BaseType')"));
        _ = new Simulation().AssertSqlError("select charindex(0x41, 'xA')", 257);
    }

    /// <summary>
    /// After a cascade has run its course, a NO ACTION key counts only the
    /// child rows still there — a row another path of the same cascade
    /// deleted doesn't block the statement, whichever table it reaches first.
    /// </summary>
    [TestMethod]
    public void NoActionKey_CheckedAfterEveryCascade()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table countries (id int primary key);
            create table eagle (id int primary key, countryid int not null references countries on delete cascade);
            create table kiwi (id int primary key, countryid int not null references countries on delete cascade, eagleid int null references eagle);
            insert countries values (1), (2); insert eagle values (1, 1), (2, 2); insert kiwi values (1, 1, 1), (2, 1, 2);
            """);
        _ = simulation.AssertSqlError("delete countries where id = 2", 547);
        _ = simulation.ExecuteNonQuery("delete kiwi where id = 2");
        _ = simulation.ExecuteNonQuery("delete countries where id = 1");
        AreEqual("1,0,0", simulation.ExecuteScalar("select concat_ws(',', (select count(*) from countries), (select count(*) from eagle where countryid = 1), (select count(*) from kiwi))"));
    }

    /// <summary>
    /// Msg 4186: OUTPUT may not read a computed column whose definition calls
    /// a function real assumes reads data — one that isn't schema-bound, or a
    /// schema-bound one that reads a table. EF catches it as its computed
    /// column with a function error.
    /// </summary>
    [TestMethod]
    public void Output_RefusesComputedColumnsOverDataAccessingFunctions()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table dbo.fb (id int primary key, f nvarchar(20), l nvarchar(20))",
            "create function dbo.getfull(@id int) returns nvarchar(max) with schemabinding as begin declare @r nvarchar(max); select @r = f + l from dbo.fb where id = @id; return @r end",
            "create function dbo.pure(@a nvarchar(20)) returns nvarchar(40) with schemabinding as begin return @a + N'!' end",
            "create function dbo.unbound(@a nvarchar(20)) returns nvarchar(40) as begin return @a + N'?' end",
            "alter table dbo.fb add full1 as dbo.getfull(id), p as dbo.pure(f), u as dbo.unbound(f)");

        AreEqual("a!", simulation.ExecuteScalar("insert dbo.fb (id, f, l) output inserted.p values (2, N'a', N'b')"));
        simulation.AssertSqlError("insert dbo.fb (id, f, l) output inserted.full1 values (1, N'a', N'b')", 4186,
            "Column 'inserted.full1' cannot be referenced in the OUTPUT clause because the column definition contains a subquery or references a function that performs user or system data access. A function is assumed by default to perform data access if it is not schemabound. Consider removing the subquery or function from the column definition or removing the column from the OUTPUT clause.");
        _ = simulation.AssertSqlError("update dbo.fb set f = N'z' output deleted.U where id = 2", 4186);
        _ = simulation.AssertSqlError("delete dbo.fb output deleted.full1 where id = 2", 4186);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from dbo.fb"));
    }

    /// <summary>
    /// A legacy LOB local variable is Msg 2739 alone, with or without an
    /// initializer: real still declares the variable, so a later reference to
    /// it adds no Msg 137.
    /// </summary>
    [TestMethod]
    [DataRow("declare @string text = N'x'; select 1 where @string is null")]
    [DataRow("declare @i image, @j int = 1; select @j")]
    public void LegacyLobVariable_IsMsg2739Alone(string batch)
    {
        var error = new Simulation().AssertSqlError(batch, 2739);
        AreEqual(1, error.Errors.Count);
    }

    /// <summary>
    /// A PRIMARY KEY written without CLUSTERED becomes nonclustered when
    /// another key of the same declaration asks for CLUSTERED; two explicit
    /// ones stay Msg 8112.
    /// </summary>
    [TestMethod]
    public void PrimaryKey_YieldsClusteredToAnExplicitUniqueConstraint()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table c1 (id1 int primary key, id2 int, constraint uk unique clustered (id2))");
        _ = simulation.ExecuteNonQuery("create table c2 (id1 int unique clustered, id2 int primary key)");
        AreEqual("c1:CLUSTERED:UNIQUE,c1:NONCLUSTERED:PRIMARY_KEY,c2:CLUSTERED:UNIQUE,c2:NONCLUSTERED:PRIMARY_KEY", simulation.ExecuteScalar(
            "select string_agg(concat(object_name(object_id), ':', type_desc, ':', iif(is_primary_key = 1, 'PRIMARY_KEY', 'UNIQUE')), ',') within group (order by object_name(object_id), index_id) from sys.indexes where object_id in (object_id('c1'), object_id('c2'))"));
        _ = simulation.AssertSqlError("create table c3 (id1 int primary key clustered, id2 int, constraint uk3 unique clustered (id2))", 8112);
    }

    /// <summary>
    /// A computed column reports its expression's collation in
    /// <c>sys.columns</c>, which EF's scaffolder reads back.
    /// </summary>
    [TestMethod]
    public void ComputedColumn_ReportsItsExpressionsCollation()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table cc (id int primary key, n nvarchar(20), c1 as 'hello' collate German_PhoneBook_CI_AS, c2 as n, c3 as n collate Latin1_General_BIN persisted, c5 as id + 1)");
        AreEqual("n:SQL_Latin1_General_CP1_CI_AS,c1:German_PhoneBook_CI_AS,c2:SQL_Latin1_General_CP1_CI_AS,c3:Latin1_General_BIN,c5:", simulation.ExecuteScalar(
            "select string_agg(concat(name, ':', collation_name), ',') within group (order by column_id) from sys.columns where object_id = object_id('cc') and name <> 'id'"));
    }

    /// <summary>
    /// <c>ALTER COLUMN</c> takes <c>SPARSE</c> after <c>COLLATE</c> and before
    /// <c>MASKED WITH</c>; a restatement without it makes the column non-sparse,
    /// and the sparse refusals come ahead of the conversion check.
    /// </summary>
    [TestMethod]
    public void AlterColumn_TakesSparse()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table s (id int primary key, v nvarchar(50) null, w int null, x int not null default 1)");
        _ = simulation.ExecuteNonQuery("alter table s alter column v nvarchar(max) collate Latin1_General_BIN sparse null");
        IsTrue((bool)simulation.ExecuteScalar("select is_sparse from sys.columns where object_id = object_id('s') and name = 'v'")!);
        _ = simulation.ExecuteNonQuery("alter table s alter column v nvarchar(max) sparse masked with (function = 'default()') null");
        _ = simulation.ExecuteNonQuery("alter table s alter column v nvarchar(max) null");
        IsFalse((bool)simulation.ExecuteScalar("select is_sparse from sys.columns where object_id = object_id('s') and name = 'v'")!);

        simulation.ValidateSyntaxError("alter table s alter column w int null sparse", "sparse");
        simulation.ValidateSyntaxError("alter table s alter column v nvarchar(max) masked with (function = 'default()') sparse null", "sparse");
        _ = simulation.AssertSqlError("alter table s alter column x int sparse null", 11410);
        _ = simulation.AssertSqlError("alter table s alter column w text sparse null", 1731);
        _ = simulation.AssertSqlError("alter table s alter column w int sparse not null", 1731);
    }
}
