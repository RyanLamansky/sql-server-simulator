using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_get_query_template</c> and the forced-parameterization form Query
/// Store stores under <c>PARAMETERIZATION FORCED</c>, which share one
/// renderer. Templates, numbering and types probed 2026-10-15 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class QueryTemplateTests
{
    private static string Template(string query) => new Simulation().ExecuteScalar($"""
        declare @t nvarchar(max), @p nvarchar(max);
        exec sp_get_query_template N'{query.Replace("'", "''", StringComparison.Ordinal)}', @t output, @p output;
        select concat(@t, ' | ', @p)
        """).ToInvariantString();

    [TestMethod]
    public void Template_ParameterizesTheWhereClausesLiteralsByType()
        => AreEqual(
            "select a , 5 , 'x' from t where b = @0 and c = @1 and d = @2 and e = @3 and f = @4 and g = @5 and h = @6 and k = @7 and m = @8"
            + " | @0 int,@1 varchar(8000),@2 nvarchar(4000),@3 numeric(38,2),@4 int,@5 int,@6 varbinary(8000),@7 float,@8 money",
            Template("SELECT a, 5, 'x' FROM t WHERE b = 3 AND c = 'hello' and d=N'uni' and e = 3.25 and f = 100000 and g=-4 and h = 0x1F and k = 1e3 and m = $5"));

    [TestMethod]
    public void Template_LowersReservedKeywordsAndSplitsOperators()
        => AreEqual(
            "select COUNT ( * ) from T with ( NOLOCK ) where A > = @0 and B < > @1 and C = convert ( INT , @2 ) option ( MAXDOP 1 ) | @0 int,@1 int,@2 varchar(8000)",
            Template("SELECT COUNT(*) FROM T WITH (NOLOCK) WHERE A >= 1 AND B <> 2 AND C = CONVERT(INT, '5') /* note */ OPTION (MAXDOP 1);"));

    [TestMethod]
    public void Template_KeepsTheClausesForcedParameterizationLeaves()
        => AreEqual(
            "select top 5 * from t where a in ( @0 , @1 ) and b like 'x%' and c = ( select x from u where y = @2 ) group by a having a > 2 order by 1 | @0 int,@1 int,@2 int",
            Template("select top 5 * from t where a in (1, 2) and b like 'x%' and c = (select x from u where y = 3) group by a having a > 2 order by 1"));

    [TestMethod]
    public void Template_NumbersButKeepsArithmeticAndConvertStyles()
        => AreEqual(
            "update t set a = a + 1 , b = convert ( varchar ( 10 ) , getdate ( ) , 120 ) where c = @2 and d = @3 and e = @4 | @2 int,@3 int,@4 int",
            Template("update t set a = a + 1, b = convert(varchar(10), getdate(), 120) where c = 4 and d = - - 5 and e = -(5)"));

    [TestMethod]
    public void Template_TypesWideLiteralsAsReal()
        => AreEqual(
            "select * from t where a = @0 and b = @1 and c = @2 and d = @3 | @0 int,@1 numeric(38,0),@2 numeric(38,1),@3 varchar(max)",
            Template($"select * from t where a = -2147483648 and b = 2147483648 and c = .5 and d = '{new string('x', 8001)}'"));

    [TestMethod]
    public void Template_RefusesWhatForcedParameterizationLeavesAsWritten()
    {
        var simulation = new Simulation();
        void Refused(string query, int number, byte state)
        {
            var error = simulation.AssertSqlError($"""
                declare @t nvarchar(max), @p nvarchar(max);
                exec sp_get_query_template N'{query.Replace("'", "''", StringComparison.Ordinal)}', @t output, @p output
                """, number);
            AreEqual((state, "sp_get_query_template", 1), (error.Errors[^1].State, error.Errors[^1].Procedure, error.Errors[^1].LineNumber), query);
        }
        Refused("", 10523, 2);
        Refused("select 1; select 2", 10523, 3);
        Refused("if 1 = 1 select 1", 10523, 3);
        Refused("select 1", 10524, 1);
        Refused("exec p 1", 10524, 1);
        Refused("select * from t where a = 1 option (recompile)", 10524, 1);
        Refused("select * from t where a = 1 + 2", 10524, 2);
        Refused("select * from t where a = {d '2020-01-01'}", 10524, 2);
    }

    [TestMethod]
    public void Template_SendsAParseErrorAheadOfItsRefusal()
    {
        var error = new Simulation().AssertSqlError("""
            declare @t nvarchar(max), @p nvarchar(max);
            exec sp_get_query_template N'select * from "t" where a = 1', @t output, @p output
            """, 102);
        CollectionAssert.AreEqual(new[] { 102, 10523 }, error.Errors.Select(static entry => entry.Number).ToArray());
    }

    [TestMethod]
    public void Template_ACaughtRefusalReadsTheParseError()
        => AreEqual(102, new Simulation().ExecuteScalar("""
            declare @t nvarchar(max), @p nvarchar(max), @n int;
            begin try exec sp_get_query_template N'select * frm t where a = 1', @t output, @p output; end try
            begin catch set @n = error_number(); end catch
            select @n
            """));

    [TestMethod]
    public void Template_ABatchAPlanGuideMatchesIsNotParameterized()
        => new Simulation().AssertSqlError("""
            create table t (a int);
            exec sp_create_plan_guide N'g', N'select * from t where a = 1', N'SQL';
            declare @t nvarchar(max), @p nvarchar(max);
            exec sp_get_query_template N'select * from t where a = 1', @t output, @p output
            """, 10524);

    [TestMethod]
    public void Template_ArgumentRefusalsCarryRealsStates()
    {
        var simulation = new Simulation();
        void Refused(string arguments, int number, byte state) =>
            AreEqual(state, simulation.AssertSqlError($"declare @t nvarchar(max), @p nvarchar(max), @v varchar(10); exec sp_get_query_template {arguments}", number).Errors[0].State, arguments);
        Refused("", 201, 20);
        Refused("N'select 1'", 201, 21);
        Refused("N'select 1', @t output", 201, 22);
        Refused("N'select 1', @t, @p", 591, 62);
        Refused("N'select 1', @t output, @p", 591, 63);
        Refused("null, @t output, @p output", 214, 21);
        Refused("N'select 1', @v output, @p output", 214, 22);
        Refused("N'select 1', @t output, @v output", 214, 23);
        Refused("N'select 1', @t output, @p output, 4", 8144, 23);
    }

    [TestMethod]
    public void Template_ReturnsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            declare @t nvarchar(max), @p nvarchar(max), @rc int;
            exec @rc = sp_get_query_template N'select * from t where a = 1', @t output, @p output;
            select @rc
            """));

    [TestMethod]
    public void QueryStore_StoresTheForcedFormUnderForcedParameterization()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "alter database d set parameterization forced",
            "alter database d set query_store = on (query_capture_mode = all)",
            "use d; create table t (a int, b varchar(10), c int)",
            "use d; update t set a = 1, b = 'q' where c = 2 and a = 7",
            "use d; select * from t where a <> 5 and b like 'x%'");
        AreEqual(
            "(@2 int,@3 int,@0 int,@1 varchar(8000))update t set a = @0 , b = @1 where c = @2 and a = @3:3:Forced|(@0 int)select * from t where a < > @0 and b like 'x%':3:Forced",
            simulation.ExecuteScalar("""
                use d;
                select string_agg(concat(qt.query_sql_text, ':', q.query_parameterization_type, ':', q.query_parameterization_type_desc), '|') within group (order by q.query_id)
                from sys.query_store_query_text qt join sys.query_store_query q on q.query_text_id = qt.query_text_id
                where qt.query_sql_text like '%t %'
                """));
    }

    /// <summary>
    /// Query Store's stored form declares each <c>IN</c> list's parameters in
    /// reverse where they stand, a value nested in the list's items included,
    /// while <c>sp_get_query_template</c> and a <c>VALUES</c> row keep number
    /// order (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select * from t where b = 7 and a in (1, 2, 3)", "(@0 int,@3 int,@2 int,@1 int)select * from t where b = @0 and a in ( @1 , @2 , @3 )")]
    [DataRow("select * from t where a in (1, 2) and b in (5, 6)", "(@1 int,@0 int,@3 int,@2 int)select * from t where a in ( @0 , @1 ) and b in ( @2 , @3 )")]
    [DataRow("select * from t where a in (1, abs(2), 3)", "(@2 int,@1 int,@0 int)select * from t where a in ( @0 , abs ( @1 ) , @2 )")]
    [DataRow("select * from t where a in (1, null, 3)", "(@1 int,@0 int)select * from t where a in ( @0 , null , @1 )")]
    [DataRow("select * from t where a in (1, 'x')", "(@1 varchar(8000),@0 int)select * from t where a in ( @0 , @1 )")]
    [DataRow("update t set b = 3, c = 'q' where a in (1, 2) and b = 4", "(@3 int,@2 int,@4 int,@0 int,@1 varchar(8000))update t set b = @0 , c = @1 where a in ( @2 , @3 ) and b = @4")]
    [DataRow("insert t (a, b) values (1, 2), (3, 4)", "(@0 int,@1 int,@2 int,@3 int)insert t ( a , b ) values ( @0 , @1 ) , ( @2 , @3 )")]
    public void QueryStore_DeclaresAnInListsParametersInReverse(string statement, string stored)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "alter database d set parameterization forced",
            "alter database d set query_store = on (query_capture_mode = all)",
            "use d; create table t (a int, b int, c varchar(10))",
            "use d; " + statement);
        AreEqual(stored, simulation.ExecuteScalar("use d; select query_sql_text from sys.query_store_query_text where query_sql_text like '(@%'"));
        AreEqual("@0 int,@1 int,@2 int,@3 int", simulation.ExecuteScalar("""
            declare @t nvarchar(max), @p nvarchar(max);
            exec sp_get_query_template N'select * from t where b = 7 and a in (1, 2, 3)', @t output, @p output;
            select @p
            """));
    }
}
