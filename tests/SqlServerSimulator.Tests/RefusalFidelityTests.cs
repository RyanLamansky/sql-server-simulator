using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Statements SQL Server 2025 refuses that the simulator once accepted, each
/// probed 2026-09-24, plus the neighboring shapes real still accepts.
/// </summary>
[TestClass]
public sealed class RefusalFidelityTests
{
    private const string Msg264 = "The column name 'a' is specified more than once in the SET clause or column list of an INSERT. A column cannot be assigned more than one value in the same clause. Modify the clause to make sure that a column is updated only once. If this statement updates or inserts columns into a view, column aliasing can conceal the duplication in your code.";

    [TestMethod]
    public void DerivedTable_RepeatedProjectionName_RaisesMsg8156()
        => new Simulation().AssertSqlError("select * from (select 1 a, 2 a) d", 8156, "The column 'a' was specified multiple times for 'd'.");

    [TestMethod]
    [DataRow("with c as (select 1 a, 2 a) select * from c")]
    [DataRow("with c(a, a) as (select 1, 2) select * from c")]
    public void Cte_RepeatedColumnName_RaisesMsg8156WhereRead(string sql)
        => new Simulation().AssertSqlError(sql, 8156, "The column 'a' was specified multiple times for 'c'.");

    [TestMethod]
    [DataRow("create table dd (a int, A int)", "Column names in each table must be unique. Column name 'A' in table 'dd' is specified more than once.", 3)]
    [DataRow("create table dd (a int, a as 1)", "Column names in each table must be unique. Column name 'a' in table 'dd' is specified more than once.", 2)]
    public void CreateTable_RepeatedColumnName_RaisesMsg2705(string sql, string message, int state)
    {
        var ex = new Simulation().AssertSqlError(sql, 2705);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual((byte)state, ex.Errors[0].State);
    }

    // ---- unary minus ----

    [TestMethod]
    [DataRow("-'1'", "varchar")]
    [DataRow("-N'1'", "nvarchar")]
    [DataRow("-(('1'))", "varchar")]
    [DataRow("-0x01", "varbinary")]
    public void UnaryMinus_OnAStringOrBinaryLiteral_RaisesMsg403(string expression, string type)
        => new Simulation().AssertSqlError($"select {expression}", 403, $"Invalid operator for data type. Operator equals minus, type equals {type}.");

    [TestMethod]
    [DataRow("declare @v varchar(5) = '1'; select -@v", "varchar")]
    [DataRow("select -cast(0x01 as varbinary(2))", "varbinary")]
    [DataRow("select -getdate()", "datetime")]
    [DataRow("select -cast(1 as sql_variant)", "sql_variant")]
    [DataRow("select -newid()", "uniqueidentifier")]
    public void UnaryMinus_OnATypedNonNumeric_RaisesMsg8117(string sql, string type)
        => new Simulation().AssertSqlError(sql, 8117, $"Operand data type {type} is invalid for minus operator.");

    [TestMethod]
    public void UnaryMinus_OnNullAndNumbers_StillWorks()
        => AreEqual("|-1|-1.5", new Simulation().ExecuteScalar("select concat(-null, '|', -1, '|', -cast(1.5 as float))"));

    // ---- a bare NULL where real needs a type ----

    [TestMethod]
    [DataRow("select nullif(null, 1)")]
    [DataRow("select nullif((null), 1)")]
    public void Nullif_BareNullFirst_RaisesMsg4151(string sql)
        => new Simulation().AssertSqlError(sql, 4151);

    [TestMethod]
    [DataRow("select coalesce(null, null)")]
    [DataRow("select coalesce((null), null, (null))")]
    public void Coalesce_AllBareNulls_RaisesMsg4127(string sql)
        => new Simulation().AssertSqlError(sql, 4127, "At least one of the arguments to COALESCE must be an expression that is not the NULL constant.");

    [TestMethod]
    [DataRow("select coalesce(null)")]
    [DataRow("select coalesce(1)")]
    public void Coalesce_OneArgument_IsASyntaxError(string sql)
        => new Simulation().AssertSqlError(sql, 102, "Incorrect syntax near ')'.");

    [TestMethod]
    public void Nullif_BareNull_RaisesBeforeAnythingRuns()
        => new Simulation().AssertSqlError("create table early (a int); select nullif(null, 1)", 4151);

    [TestMethod]
    public void Substring_BareNullString_RaisesMsg8116()
        => new Simulation().AssertSqlError("select substring(null, 1, 1)", 8116, "Argument data type NULL is invalid for argument 1 of substring function.");

    [TestMethod]
    public void Substring_NullStartOrLength_AnswersNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select substring('a', null, 1)"));

    [TestMethod]
    public void Checksum_ReportsEveryBareNull()
    {
        var ex = new Simulation().AssertSqlError("select checksum(null, 1, null)", 8116);
        AreEqual(2, ex.Errors.Count);
        AreEqual("Argument data type NULL is invalid for argument 3 of checksum function.", ex.Errors[1].Message);
        AreEqual((byte)4, ex.Errors[0].State);
    }

    [TestMethod]
    public void BinaryChecksum_AllBareNulls_RaisesMsg8184()
        => new Simulation().AssertSqlError("select binary_checksum(null, null)", 8184);

    [TestMethod]
    public void BinaryChecksum_SomeBareNulls_Runs()
        => IsNotNull(new Simulation().ExecuteScalar("select binary_checksum(1, null)"));

    // ---- lowercase n before a quote ----

    [TestMethod]
    public void LowercaseN_IsAColumnFollowedByAnAlias()
        => AreEqual(5, new Simulation().ExecuteScalar("select n'x' from (select 5 as n) d"));

    [TestMethod]
    public void LowercaseN_WithoutAColumn_RaisesMsg207()
        => new Simulation().AssertSqlError("select n'x'", 207, "Invalid column name 'n'.");

    // ---- ORDER BY and the select list ----

    private const string OrderRows = "create table obt (a int, b int); insert obt values (1, 20), (2, 10);";

    [TestMethod]
    [DataRow("x + 1")]
    [DataRow("-x")]
    [DataRow("case when x > 1 then 1 else 0 end")]
    public void OrderBy_AliasInsideAnExpression_RaisesMsg207(string term)
        => new Simulation().AssertSqlError($"{OrderRows} select b as x from obt order by {term}", 207, "Invalid column name 'x'.");

    [TestMethod]
    public void OrderBy_AliasInsideAnExpression_ReadsTheSourceColumnOfThatName()
        => AreEqual(20, new Simulation().ExecuteScalar($"{OrderRows} select top 1 b as a from obt order by a + 0"));

    [TestMethod]
    [DataRow("x")]
    [DataRow("(x)")]
    public void OrderBy_BareAlias_StillSorts(string term)
        => AreEqual(10, new Simulation().ExecuteScalar($"{OrderRows} select top 1 b as x from obt order by {term}"));

    [TestMethod]
    public void OrderBy_AliasOfAnAggregateInsideAnExpression_RaisesMsg207()
        => new Simulation().AssertSqlError($"{OrderRows} select sum(b) as s from obt group by a order by s + 1", 207);

    [TestMethod]
    public void OrderBy_AliasExpressionUnderDistinct_AddsMsg145()
    {
        var ex = new Simulation().AssertSqlError($"{OrderRows} select distinct b as x from obt order by x + 1", 207);
        AreEqual(145, ex.Errors[1].Number);
    }

    [TestMethod]
    [DataRow("select 1 order by 2", 2)]
    [DataRow("select 1 as a order by 0", 0)]
    public void OrderBy_PositionPastAFromlessSelectList_RaisesMsg108(string sql, int position)
        => new Simulation().AssertSqlError(sql, 108, $"The ORDER BY position number {position} is out of range of the number of items in the select list.");

    // ---- one column assigned twice ----

    [TestMethod]
    [DataRow("update u2 set a = 1, A = 2")]
    [DataRow("update u2 set a = 1, b = 2, a = 3 where 1 = 0")]
    [DataRow("update u2 set a += 1, a = 2")]
    [DataRow("update u2 set u2.a = 1, a = 2")]
    [DataRow("insert u2 (a, A) values (1, 2)")]
    [DataRow("merge u2 using (select 1 x) s on u2.a = s.x when matched then update set a = 1, A = 2;")]
    [DataRow("merge u2 using (select 1 x) s on u2.a = s.x when not matched then insert (a, A) values (1, 2);")]
    public void ColumnAssignedTwice_RaisesMsg264(string statement)
        => new Simulation().AssertSqlError($"create table u2 (a int, b int); {statement}", 264, Msg264);

    [TestMethod]
    public void VariableAssignedTwice_IsFine()
        => AreEqual(11, new Simulation().ExecuteScalar("""
            create table u3 (a int, b int); insert u3 values (1, 1);
            declare @v int = 5; update u3 set @v = b, @v = @v + 10; select @v
            """));

    [TestMethod]
    public void VariableSetFromAColumnAtTheEndOfTheBatch_Parses()
        => AreEqual(0, new Simulation().ExecuteNonQuery("create table u4 (a int, b int); declare @v int; update u4 set @v = a, @v = b"));

    // ---- TRUNCATE and DROP against foreign keys ----

    [TestMethod]
    [DataRow("")]
    [DataRow("alter table fkc nocheck constraint all;")]
    public void Truncate_ReferencedTable_RaisesMsg4712(string disable)
        => new Simulation().AssertSqlError($"""
            create table fkp (id int primary key); create table fkc (p int references fkp(id));
            {disable} truncate table fkp
            """, 4712, "Cannot truncate table 'fkp' because it is being referenced by a FOREIGN KEY constraint.");

    [TestMethod]
    public void SelfReferencingTable_TruncatesAndDrops()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table fks (id int primary key, p int references fks(id));
            insert fks values (1, null), (2, 1);
            truncate table fks;
            drop table fks;
            select count(*) from sys.tables where name = 'fks'
            """));

    // ---- sp_executesql's argument types and counts ----

    [TestMethod]
    [DataRow("exec sp_executesql 'select 1'")]
    [DataRow("exec sp_executesql 1")]
    [DataRow("exec sp_executesql null")]
    [DataRow("declare @s char(20) = 'select 1'; exec sp_executesql @s")]
    public void SpExecuteSql_NonUnicodeStatement_RaisesMsg214(string sql)
        => new Simulation().AssertSqlError(sql, 214, "Procedure expects parameter '@statement' of type 'ntext/nchar/nvarchar'.");

    [TestMethod]
    [DataRow("exec sp_executesql N'select @a', '@a int', 1")]
    [DataRow("exec sp_executesql N'select 1', 5")]
    [DataRow("exec sp_executesql N'select 1', null, 1")]
    public void SpExecuteSql_NonUnicodeDeclarations_RaisesMsg214State3(string sql)
    {
        var ex = new Simulation().AssertSqlError(sql, 214);
        AreEqual("Procedure expects parameter '@params' of type 'ntext/nchar/nvarchar'.", ex.Errors[0].Message);
        AreEqual((byte)3, ex.Errors[0].State);
    }

    [TestMethod]
    public void SpExecuteSql_UnicodeVariableStatement_Runs()
        => AreEqual(1, new Simulation().ExecuteScalar("declare @s nchar(20) = N'select 1'; exec sp_executesql @s"));

    [TestMethod]
    [DataRow("@a = 1, @a = 2")]
    [DataRow("1, 2")]
    [DataRow("@a = 1, @b = 2")]
    public void SpExecuteSql_SurplusArgument_RaisesMsg8144(string arguments)
        => new Simulation().AssertSqlError($"exec sp_executesql N'select @a', N'@a int', {arguments}", 8144, "Procedure or function  has too many arguments specified.");

    [TestMethod]
    public void SpExecuteSql_ArgumentsWithoutDeclarations_RaisesMsg8146()
        => new Simulation().AssertSqlError("exec sp_executesql N'select 1', N'', 1", 8146, "Procedure  has no parameters and arguments were supplied.");

    [TestMethod]
    public void SpExecuteSql_SkippedBranch_DoesNotRaise()
        => AreEqual(-1, new Simulation().ExecuteNonQuery("if 1 = 0 exec sp_executesql 'select 1'"));

    // ---- transaction names ----

    [TestMethod]
    [DataRow("begin tran abcdefghijabcdefghijabcdefghijabc")]
    [DataRow("begin tran [abcdefghijabcdefghijabcdefghijabc]")]
    [DataRow("save tran abcdefghijabcdefghijabcdefghijabc")]
    [DataRow("begin tran t; commit tran abcdefghijabcdefghijabcdefghijabc")]
    [DataRow("rollback tran abcdefghijabcdefghijabcdefghijabc")]
    public void TransactionName_Past32Characters_RaisesMsg103(string sql)
        => new Simulation().AssertSqlError(sql, 103, "The identifier that starts with 'abcdefghijabcdefghijabcdefghijabc' is too long. Maximum length is 32.");

    [TestMethod]
    public void RollbackTran_NamingTheOutermostTransaction_RollsItAllBack()
        => AreEqual("0|0", new Simulation().ExecuteScalar("""
            create table rbt (a int);
            begin tran Outer1; begin tran; insert rbt values (1); rollback tran Outer1;
            select concat(@@trancount, '|', (select count(*) from rbt))
            """));

    [TestMethod]
    public void RollbackTran_NamingTheOutermostTransactionFromAVariable_RollsItBack()
        => AreEqual(0, new Simulation().ExecuteScalar("declare @n varchar(40) = 'Outer1'; begin tran @n; rollback tran Outer1; select @@trancount"));

    [TestMethod]
    [DataRow("begin tran Outer1; begin tran Inner1; rollback tran Inner1")]
    [DataRow("begin tran Outer1; rollback tran outer1")]
    [DataRow("begin tran; rollback tran Outer1")]
    public void RollbackTran_NamingAnythingElse_RaisesMsg6401(string sql)
        => new Simulation().AssertSqlError(sql, 6401);

    // ---- whitespace around a number being converted ----

    [TestMethod]
    [DataRow("int", "char(9) + '1'")]
    [DataRow("int", "'1' + char(10)")]
    [DataRow("bigint", "char(13) + '1'")]
    [DataRow("int", "nchar(160) + N'1'")]
    [DataRow("decimal(5, 1)", "char(9) + '1.5'")]
    [DataRow("bit", "'1' + char(9)")]
    [DataRow("float", "'1.5' + char(9)")]
    [DataRow("real", "char(160) + '1.5'")]
    [DataRow("money", "char(9) + '1.5'")]
    [DataRow("uniqueidentifier", "char(9) + '00000000-0000-0000-0000-000000000000'")]
    [DataRow("float", "char(9)")]
    public void Conversion_RefusesWhitespaceRealDoesNotTrim(string type, string expression)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"select try_cast({expression} as {type})"));

    [TestMethod]
    [DataRow("int", "' 1 '", "1")]
    [DataRow("float", "char(9) + char(10) + '1.5'", "1.5")]
    [DataRow("float", "char(160) + '1.5'", "1.5")]
    [DataRow("real", "nchar(160) + N'1.5'", "1.5")]
    [DataRow("money", "'1.5' + char(9) + char(160)", "1.50")]
    [DataRow("uniqueidentifier", "'00000000-0000-0000-0000-000000000000' + char(9)", "00000000-0000-0000-0000-000000000000")]
    [DataRow("float", "'  '", "0")]
    public void Conversion_TrimsTheWhitespaceRealTrims(string type, string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast(cast({expression} as {type}) as varchar(40))"));

    [TestMethod]
    public void Conversion_TabBeforeAnInteger_RaisesMsg245()
        => new Simulation().AssertSqlError("select cast(char(9) + '1' as int)", 245, "Conversion failed when converting the varchar value '\t1' to data type int.");

    [TestMethod]
    [DataRow("select try_cast('x' as money)")]
    [DataRow("select try_convert(smallmoney, 'abc')")]
    public void TryCast_UnreadableMoney_IsNull(string sql)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(sql));

    // ---- unclosed delimiters ----

    [TestMethod]
    [DataRow("select [abc", "abc")]
    [DataRow("select 1 as [x", "x")]
    [DataRow("select 'abc", "abc")]
    [DataRow("select N'x", "x")]
    public void UnclosedDelimiter_RaisesMsg105ThenMsg102(string sql, string body)
    {
        var ex = new Simulation().AssertSqlError(sql, 105);
        AreEqual($"Unclosed quotation mark after the character string '{body}'.", ex.Errors[0].Message);
        AreEqual($"Incorrect syntax near '{body}'.", ex.Errors[1].Message);
    }

    // ---- CHECK constraints bind while compiling ----

    [TestMethod]
    [DataRow("create table ck (a int, constraint c1 check (cast(a as date) is null))")]
    [DataRow("create table ck (a int check (a > 0), b date check (cast(b as int) > 0))")]
    [DataRow("create table ck (a int); alter table ck add constraint c1 check (cast(a as date) is null)")]
    public void Check_IllegalConversion_RaisesMsg529ThenMsg1750(string sql)
    {
        var ex = new Simulation().AssertSqlError(sql, 529);
        AreEqual(1750, ex.Errors[1].Number);
    }

    [TestMethod]
    [DataRow("create table ck (a int check (zz > 0))")]
    [DataRow("create table ck (a int); alter table ck add check (zz > 0)")]
    public void Check_UnknownColumn_RaisesMsg207(string sql)
        => new Simulation().AssertSqlError(sql, 207, "Invalid column name 'zz'.");

    // ---- argument lists ----

    [TestMethod]
    [DataRow("select isnull(1, 2,)", 102, "Incorrect syntax near ')'.")]
    [DataRow("select isnull(1, 2, 3, 4)", 174, "The isnull function requires 2 argument(s).")]
    public void Isnull_SurplusArguments_ParseBeforeTheyAreCounted(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);

    [TestMethod]
    [DataRow("declare @o int = -1; select lag(1, @o) over (order by (select 1))", 2)]
    [DataRow("select lag(1, -1) over (order by (select 1))", 1)]
    [DataRow("select lead(1, 1 - 2) over (order by (select 1))", 1)]
    public void LagLead_NegativeOffset_RaisesMsg8730(string sql, int state)
    {
        var ex = new Simulation().AssertSqlError(sql, 8730);
        AreEqual((byte)state, ex.Errors[0].State);
    }

    [TestMethod]
    public void InsertValues_PastAThousandRows_RaisesMsg10738()
        => new Simulation().AssertSqlError(
            $"create table vv (a int); insert vv values {string.Join(", ", Enumerable.Range(0, 1001).Select(i => $"({i})"))}",
            10738);

    [TestMethod]
    public void ValuesConstructorInFrom_TakesMoreThanAThousandRows()
        => AreEqual(1001, new Simulation().ExecuteScalar(
            $"select count(*) from (values {string.Join(", ", Enumerable.Range(0, 1001).Select(i => $"({i})"))}) x(a)"));

    // ---- STRING_AGG's separator and the offset functions' operand ----

    private const string AggRows = "create table sa (s varchar(10), n nvarchar(10), i int); insert sa values ('a', N'x', 1), ('b', N'y', 2);";

    [TestMethod]
    [DataRow("string_agg(s, n)", "nvarchar")]
    [DataRow("string_agg(s, N',')", "nvarchar")]
    [DataRow("string_agg(s, 1)", "int")]
    [DataRow("string_agg(s, 0x2c)", "varbinary")]
    public void StringAgg_SeparatorOfTheWrongType_RaisesMsg8116(string call, string type)
        => new Simulation().AssertSqlError($"{AggRows} select {call} from sa", 8116, $"Argument data type {type} is invalid for argument 2 of string_agg function.");

    [TestMethod]
    [DataRow("string_agg(s, s)")]
    [DataRow("string_agg(s, upper(','))")]
    public void StringAgg_SeparatorNeitherConstantNorVariable_RaisesMsg8733(string call)
        => new Simulation().AssertSqlError($"{AggRows} select {call} from sa where 1 = 0", 8733, "Separator parameter for STRING_AGG must be a string literal or variable.");

    [TestMethod]
    [DataRow("string_agg(s, ',' + ',')", "a,,b")]
    [DataRow("string_agg(s, char(44))", "a,b")]
    [DataRow("string_agg(s, (','))", "a,b")]
    [DataRow("string_agg(s, null)", "ab")]
    [DataRow("string_agg(n, ',')", "x,y")]
    [DataRow("string_agg(i, N',')", "1,2")]
    public void StringAgg_ConstantOrVariableSeparator_Aggregates(string call, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"{AggRows} select {call} within group (order by i) from sa"));

    [TestMethod]
    [DataRow("lag(null)", "lag")]
    [DataRow("lead((null), 1, 5)", "lead")]
    [DataRow("first_value(null)", "first_value")]
    [DataRow("last_value(null)", "last_value")]
    public void OffsetAndValueFunctions_BareNullOperand_RaisesMsg8117(string call, string name)
        => new Simulation().AssertSqlError($"select {call} over (order by (select 1))", 8117, $"Operand data type NULL is invalid for {name} operator.");

    // ---- a SELECT with no FROM clause to expand a star against ----

    [TestMethod]
    [DataRow("select *")]
    [DataRow("select 1, *")]
    [DataRow("select count(*), *")]
    public void FromlessStar_RaisesMsg263(string sql)
        => new Simulation().AssertSqlError(sql, 263, "Must specify table to select from.");

    [TestMethod]
    [DataRow("select t.*")]
    [DataRow("select 1 where exists (select t.*)")]
    public void FromlessQualifiedStar_RaisesMsg107(string sql)
        => new Simulation().AssertSqlError(sql, 107, "The column prefix 't' does not match with a table name or alias name used in the query.");

    [TestMethod]
    public void FromlessStarInExists_IsNeverRead()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 where exists (select *)"));

    [TestMethod]
    [DataRow("00000000300", "int", 10)]
    [DataRow("000000000300", "numeric", 3)]
    [DataRow("000000000001", "numeric", 1)]
    [DataRow("0000000002147483647", "numeric", 10)]
    public void IntegerLiteral_WrittenPastElevenCharacters_IsNumeric(string literal, string type, int precision)
        => AreEqual($"{type}|{precision}", new Simulation().ExecuteScalar($"select concat(sql_variant_property({literal}, 'BaseType'), '|', sql_variant_property({literal}, 'Precision'))"));

    // ---- an erroring non-persisted computed column (probed 2026-09-24 against SQL Server 2025) ----

    [TestMethod]
    public void ErroringNonPersistedComputedColumn_FailsOnlyTheReadThatProjectsIt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table cz (a int, b as 1 / a); insert cz (a) values (0); update cz set a = 0");
        AreEqual(0, sim.ExecuteScalar("select a from cz"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from cz"));
        _ = sim.AssertSqlError("select * from cz", 8134);
        _ = sim.AssertSqlError("insert cz (a) output inserted.b values (0)", 8134);
        AreEqual(0, sim.ExecuteScalar("insert cz (a) output inserted.a values (0)"));
    }

    [TestMethod]
    [DataRow("create table cz (a int, b as 1 / a persisted)")]
    [DataRow("create table cz (a int, b as 1 / a); create index ix on cz(b)")]
    public void ErroringPersistedOrIndexedComputedColumn_FailsTheWrite(string create)
        => new Simulation().AssertSqlError($"{create}; insert cz (a) values (0)", 8134);

    [TestMethod]
    [DataRow("select 1 from ib where r in (select v from ib)")]
    [DataRow("select 1 from ib where r = any (select v from ib)")]
    [DataRow("select 1 from ib where v in (select r from ib)")]
    public void InSubquery_IllegalConversion_NamesTheColumnInMsg260(string query)
        => new Simulation().AssertSqlError($"create table ib (v varchar(10), r rowversion); {query}", 260,
            "Disallowed implicit conversion from data type varchar to data type timestamp, table 'ib', column 'v'. Use the CONVERT function to run this query.");

    [TestMethod]
    [DataRow("with c as (select 1 a) select * from c with c2 as (select 2 b) select * from c2", 336)]
    [DataRow("create table t336 (a int); select * from t336 with c2 as (select 2 b) select * from c2", 336)]
    [DataRow("select 1 with c2 as (select 2 b) select * from c2", 319)]
    [DataRow("create table t336 (a int); select * from t336 with (nolock) with c2 as (select 2 b) select * from c2", 319)]
    public void CteAfterAnUnterminatedStatement_NamesTheCteAfterAFromSource(string sql, int number)
        => new Simulation().AssertSqlError(sql, number);
}
