namespace SqlServerSimulator;

/// <summary>
/// <c>COLUMNPROPERTY</c> across its documented properties, the sites a name
/// resolves to (table, view, table-valued-function result, parameter, return
/// value) and the built-in types. Every expectation probed 2026-09-26 against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ColumnPropertyMatrixTests
{
    private static readonly Simulation Objects = Build();

    private static Simulation Build()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table t (id int identity(1,1) not for replication not null constraint pk_t primary key, n nvarchar(20) null,
                d decimal(10,3) not null, f float, cp as id * 2 persisted, cf as cast(f as float) * 2, cn as getdate(),
                g uniqueidentifier rowguidcol, sp int sparse null, x xml, doc varbinary(max), ext char(4),
                vf datetime2 generated always as row start hidden not null, vt datetime2 generated always as row end not null,
                period for system_time (vf, vt), b binary(6), tm time(3), dto datetimeoffset(4), r real, tx text, v sql_variant)
            """,
            "create fulltext catalog cat as default",
            "create fulltext index on t (doc type column ext, n) key index pk_t",
            "create view v as select id, n, d, cp from dbo.t",
            "create procedure p @a int, @b varchar(10) output, @c cursor varying output as select 1",
            "create function f(@a int, @b decimal(5,2)) returns decimal(7,2) as begin return @a + @b end",
            "create function mtf(@a int) returns @r table (k int not null, z varchar(5)) as begin return end");
        return sim;
    }

    [TestMethod]
    [DataRow("t", "id", "IsIdNotForRepl", "1")]
    [DataRow("t", "vf", "IsHidden", "1")]
    [DataRow("t", "vt", "GeneratedAlwaysType", "2")]
    [DataRow("t", "doc", "FullTextTypeColumn", "12")]
    [DataRow("t", "n", "FullTextTypeColumn", "-1")]
    [DataRow("t", "d", "FullTextTypeColumn", "0")]
    [DataRow("t", "n", "IsFulltextIndexed", "1")]
    [DataRow("t", "cn", "IsDeterministic", "0")]
    [DataRow("t", "cf", "IsPrecise", "0")]
    [DataRow("t", "cf", "IsIndexable", "0")]
    [DataRow("t", "x", "IsIndexable", "0")]
    [DataRow("t", "x", "IsXmlIndexable", "1")]
    [DataRow("t", "d", "IsDeterministic", "NULL")]
    [DataRow("t", "f", "Precision", "53")]
    [DataRow("t", "r", "Precision", "24")]
    [DataRow("t", "vf", "Precision", "27")]
    [DataRow("t", "tm", "Precision", "12")]
    [DataRow("t", "dto", "Precision", "31")]
    [DataRow("t", "g", "Precision", "16")]
    [DataRow("t", "cn", "Scale", "3")]
    [DataRow("t", "tm", "Scale", "3")]
    [DataRow("t", "n", "Scale", "NULL")]
    [DataRow("t", "f", "Scale", "NULL")]
    [DataRow("t", "doc", "CharMaxLen", "-1")]
    [DataRow("t", "b", "CharMaxLen", "6")]
    [DataRow("t", "tx", "CharMaxLen", "2147483647")]
    [DataRow("t", "v", "CharMaxLen", "0")]
    [DataRow("t", "ext", "UsesAnsiTrim", "1")]
    [DataRow("t", "b", "UsesAnsiTrim", "1")]
    [DataRow("t", "n", "UsesAnsiTrim", "NULL")]
    [DataRow("t", "id", "UsesAnsiTrim", "NULL")]
    [DataRow("t", "id", "StatisticalSemantics", "0")]
    [DataRow("v", "id", "IsIdentity", "1")]
    [DataRow("v", "d", "IsIndexable", "0")]
    [DataRow("v", "d", "UserDataAccess", "1")]
    [DataRow("v", "d", "IsXmlIndexable", "NULL")]
    [DataRow("mtf", "k", "AllowsNull", "0")]
    [DataRow("mtf", "z", "UsesAnsiTrim", "1")]
    [DataRow("mtf", "k", "IsIndexable", "NULL")]
    [DataRow("mtf", "@a", "IsComputed", "NULL")]
    [DataRow("p", "@b", "IsOutParam", "1")]
    [DataRow("p", "@c", "IsCursorType", "1")]
    [DataRow("p", "@b", "CharMaxLen", "10")]
    [DataRow("p", "@a", "IsIndexable", "NULL")]
    [DataRow("f", "", "ColumnId", "0")]
    [DataRow("f", "", "IsOutParam", "1")]
    [DataRow("f", "", "Precision", "7")]
    [DataRow("f", "@b", "Scale", "2")]
    public void AProperty_AnswersForTheSitesItConcerns(string objectName, string column, string property, string expected)
        => Assert.AreEqual(expected, Objects.ExecuteScalar(
            $"select isnull(cast(columnproperty(object_id('{objectName}'), '{column}', '{property}') as varchar), 'NULL')"));

    [TestMethod]
    public void SchemaBoundViewColumns_AreVerifiedAndReadNoData()
    {
        // A plain float column passes through as indexable, a float expression
        // doesn't, and neither is precise (probed 2026-10-02 against SQL
        // Server 2025).
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int, f float)", "create view dbo.v with schemabinding as select a, f, f * 2 as f2 from dbo.t");
        Assert.AreEqual("a:1/1/1/0/0/1,f:1/0/1/0/0/1,f2:1/0/1/0/0/0", sim.ExecuteScalar("""
            select string_agg(concat(name, ':', columnproperty(object_id, name, 'IsDeterministic'), '/', columnproperty(object_id, name, 'IsPrecise'), '/',
                columnproperty(object_id, name, 'IsSystemVerified'), '/', columnproperty(object_id, name, 'SystemDataAccess'), '/',
                columnproperty(object_id, name, 'UserDataAccess'), '/', columnproperty(object_id, name, 'IsIndexable')), ',') within group (order by column_id)
            from sys.columns where object_id = object_id('dbo.v')
            """));
    }

    [TestMethod]
    public void CharMaxLen_AVectorReadsItsStorageLength()
        => Assert.AreEqual(20, new Simulation().ExecuteScalar("create table t (v vector(3)); select columnproperty(object_id('t'), 'v', 'Charmaxlen')"));
}
