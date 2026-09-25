using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Errors whose number, state or class real reports differently by the shape
/// that raised them — probed 2026-09-24 against SQL Server 2025, every case
/// asserting real's number, state and wording.
/// </summary>
[TestClass]
public sealed class ErrorFidelityTests
{
    private static void AssertError(string sql, int number, byte state, string message)
    {
        var error = new Simulation().AssertSqlError(sql, number).Errors[0];
        AreEqual(state, error.State);
        AreEqual(message, error.Message);
    }

    // ---- precision and scale by where the type is written ----

    [TestMethod]
    [DataRow("declare @d decimal(39)", "Column or parameter #1: Specified column precision 39 is greater than the maximum precision of 38.")]
    [DataRow("declare @a int; declare @d decimal(39)", "Column or parameter #2: Specified column precision 39 is greater than the maximum precision of 38.")]
    [DataRow("declare @f float(100)", "Column or parameter #1: Specified column precision 100 is greater than the maximum precision of 53.")]
    [DataRow("create table t (a int, d decimal(39, 0))", "Column or parameter #2: Specified column precision 39 is greater than the maximum precision of 38.")]
    [DataRow("create table t (a int, b int, f float(60))", "Column or parameter #3: Specified column precision 60 is greater than the maximum precision of 53.")]
    [DataRow("create procedure p @a int, @d decimal(39) as select 1", "Column or parameter #2: Specified column precision 39 is greater than the maximum precision of 38.")]
    public void PrecisionPastTheMaximum_RaisesMsg2750(string sql, string message)
        => AssertError(sql, 2750, 1, message);

    [TestMethod]
    [DataRow("select cast(1 as decimal(39, 0))", "The size (39) given to the type 'decimal' exceeds the maximum allowed (38).")]
    [DataRow("select cast(1 as numeric(39, 1))", "The size (39) given to the type 'numeric' exceeds the maximum allowed (38).")]
    [DataRow("declare @a int, @d decimal(39, 0)", "The size (39) given to the type 'decimal' exceeds the maximum allowed (38).")]
    [DataRow("create function f (@d decimal(40, 0)) returns int as begin return 1 end", "The size (40) given to the type 'decimal' exceeds the maximum allowed (38).")]
    public void PrecisionWithAScalePastTheMaximum_RaisesMsg2717(string sql, string message)
        => AssertError(sql, 2717, 1, message);

    [TestMethod]
    public void CastPrecisionAlone_ClampsToTheMaximum()
        => AreEqual("numeric|38|0|float", new Simulation().ExecuteScalar("""
            select concat(cast(sql_variant_property(cast(1.5 as numeric(39)), 'BaseType') as varchar(20)), '|',
                          cast(sql_variant_property(cast(1.5 as numeric(39)), 'Precision') as varchar(20)), '|',
                          cast(sql_variant_property(cast(1.5 as numeric(39)), 'Scale') as varchar(20)), '|',
                          cast(sql_variant_property(cast(1 as float(54)), 'BaseType') as varchar(20)))
            """));

    [TestMethod]
    public void AddedColumnPrecision_IsNumberedWithinTheTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        sim.AssertSqlError("alter table t add e decimal(39, 0)", 2750, "Column or parameter #2: Specified column precision 39 is greater than the maximum precision of 38.");
    }

    [TestMethod]
    public void AliasTypePrecision_IsNumberedZero_AndFollowedByMsg225()
    {
        var ex = new Simulation().AssertSqlError("create type ty from decimal(39)", 2750);
        AreEqual("Column or parameter #0: Specified column precision 39 is greater than the maximum precision of 38.", ex.Errors[0].Message);
        AreEqual("The parameters supplied for the UDT \"ty\" are not valid.", ex.Errors[1].Message);
    }

    [TestMethod]
    [DataRow("select cast(1 as decimal(2, 3))")]
    [DataRow("declare @d decimal(38, 39)")]
    [DataRow("create procedure p @d decimal(2, 3) as select 1")]
    public void ScalePastPrecision_OutsideAColumn_RaisesMsg192(string sql)
        => AssertError(sql, 192, 1, "The scale must be less than or equal to the precision.");

    [TestMethod]
    [DataRow("create table t (a int, d decimal(2, 3))", "The scale (3) for column 'd' must be within the range 0 to 2.")]
    [DataRow("declare @t table (a int, d decimal(2, 3))", "The scale (3) for column 'd' must be within the range 0 to 2.")]
    public void ScalePastPrecision_InAColumn_RaisesMsg183(string sql, string message)
        => AssertError(sql, 183, 1, message);

    [TestMethod]
    public void RefusedDeclare_StillDeclaresTheVariable()
        => new Simulation().AssertSqlError("declare @f float(54) = 1; select @f", 2750);

    // ---- TOP / OFFSET / FETCH counts ----

    [TestMethod]
    [DataRow("declare @n int = null; select top (@n) 1", 1014)]
    [DataRow("declare @n int = null; select 1 order by 1 offset 0 rows fetch next @n rows only", 1014)]
    [DataRow("create table t (a int); declare @n int = null; delete top (@n) from t", 1014)]
    [DataRow("declare @n decimal(5, 0) = null; select top (@n) 1", 1060)]
    [DataRow("select top (cast(null as int)) 1", 1060)]
    [DataRow("select top ('1') 1", 1060)]
    [DataRow("select top '1' 1", 102)]
    [DataRow("select top (-1) 1", 127)]
    [DataRow("declare @n int = -1; select top (@n) 1", 127)]
    [DataRow("declare @n int = -1; select 1 order by 1 offset 0 rows fetch next @n rows only", 127)]
    [DataRow("select 1 order by 1 offset 0 rows fetch next -1 rows only", 10744)]
    [DataRow("select 1 order by 1 offset 1.5 rows", 10743)]
    [DataRow("declare @n int = null; select 1 order by 1 offset @n rows", 10743)]
    [DataRow("select 1 order by 1 offset '1' rows", 10743)]
    public void RowCount_RaisesRealsError(string sql, int number)
        => new Simulation().AssertSqlError(sql, number);

    [TestMethod]
    public void FetchOfAZeroVariable_FetchesNothing()
        => IsNull(new Simulation().ExecuteScalar("declare @n int = 0; select 1 order by 1 offset 0 rows fetch next @n rows only"));

    [TestMethod]
    public void NegativeDmlTop_IsFollowedByMsg3621()
    {
        var ex = new Simulation().AssertSqlError("create table t (a int); declare @n int = -1; update top (@n) t set a = 1", 127);
        AreEqual(3621, ex.Errors[^1].Number);
    }

    // ---- window functions ----

    [TestMethod]
    [DataRow("row_number() over ()", "row_number")]
    [DataRow("ntile(2) over ()", "ntile")]
    [DataRow("cume_dist() over ()", "cume_dist")]
    [DataRow("lead(a) over (partition by a)", "lead")]
    [DataRow("first_value(a) over ()", "first_value")]
    [DataRow("last_value(a) over (partition by a rows unbounded preceding)", "last_value")]
    public void OverWithoutOrderBy_RaisesMsg4112(string window, string function)
        => AssertError($"select {window} from (values (1)) t(a)", 4112, 1, $"The function '{function}' must have an OVER clause with ORDER BY.");

    [TestMethod]
    [DataRow("rank() over (partition by a rows unbounded preceding)", "rank", (byte)3)]
    [DataRow("cume_dist() over (order by a rows unbounded preceding)", "cume_dist", (byte)3)]
    [DataRow("lag(a) over (order by a rows unbounded preceding)", "lag", (byte)1)]
    [DataRow("lag(a) over (partition by a rows unbounded preceding)", "lag", (byte)1)]
    public void FrameOnAFrameRefusingFunction_RaisesMsg10752(string window, string function, byte state)
        => AssertError($"select {window} from (values (1)) t(a)", 10752, state, $"The function '{function}' may not have a window frame.");

    [TestMethod]
    public void FrameAsTheOnlyElement_IsASyntaxError()
        => AssertError("select lag(a) over (rows unbounded preceding) from (values (1)) t(a)", 102, 1, "Incorrect syntax near 'rows'.");

    // ---- variables ----

    [TestMethod]
    [DataRow("declare @t table (a int); select @t")]
    [DataRow("declare @t table (a int); set @t = 1")]
    [DataRow("declare @t table (a int); select @t.a from @t")]
    public void TableVariableAsAScalar_RaisesMsg137AtClass16(string sql)
    {
        var error = new Simulation().AssertSqlError(sql, 137).Errors[0];
        AreEqual((byte)16, error.Class);
        AreEqual((byte)1, error.State);
    }

    [TestMethod]
    [DataRow("declare @i int; select @i.a", "int")]
    [DataRow("declare @s varchar(9); select @s.foo()", "varchar")]
    public void MemberOfAScalarVariable_RaisesMsg258(string sql, string type)
        => AssertError(sql, 258, 1, $"Cannot call methods on {type}.");

    // ---- keyword naming ----

    [TestMethod]
    [DataRow("values (1)", "Incorrect syntax near the keyword 'values'.")]
    [DataRow("create table t (a int); delete t order by a", "Incorrect syntax near the keyword 'order'.")]
    [DataRow("create table t (a int); select a from t where a is not not null", "Incorrect syntax near the keyword 'not'.")]
    public void ReservedKeywordAtTheCursor_RaisesMsg156(string sql, string message)
        => AssertError(sql, 156, 1, message);

    [TestMethod]
    [DataRow("begin end", "Incorrect syntax near 'end'.")]
    [DataRow("select 1 end", "Incorrect syntax near 'end'.")]
    [DataRow("begin try end try begin catch select 1 end catch", "Incorrect syntax near 'try'.")]
    [DataRow("select", "Incorrect syntax near 'select'.")]
    public void StatementPositionEndAndEndOfInput_KeepMsg102(string sql, string message)
        => AssertError(sql, 102, 1, message);

    [TestMethod]
    public void CaseEnd_IsNamedAsAKeyword()
        => AssertError("select case end", 156, 1, "Incorrect syntax near the keyword 'end'.");

    [TestMethod]
    [DataRow("select count(*) from t a zzz", "zzz")]
    [DataRow("select count(*) from t a hash join t b on b.a = a.a", "hash")]
    [DataRow("select a from t where 1 = 1 zzz", "zzz")]
    [DataRow("select a from t order by a zzz", "zzz")]
    public void StrayWordAfterASelect_RaisesMsg102(string select, string word)
        => AssertError($"create table t (a int); {select}", 102, 1, $"Incorrect syntax near '{word}'.");

    // ---- DROP INDEX ----

    [TestMethod]
    [DataRow("drop index ixx")]
    [DataRow("drop index if exists ixx")]
    [DataRow("drop index ixx, iyy")]
    public void OnePartDropIndex_RaisesMsg159(string sql)
        => AssertError(sql, 159, 1, "Must specify the table name and index name for the DROP INDEX statement.");

    [TestMethod]
    [DataRow("drop index ixz on dx", "dx.ixz", (byte)7)]
    [DataRow("drop index dbo.dx.ixz", "dbo.dx.ixz", (byte)7)]
    [DataRow("drop index nope.ixz", "nope.ixz", (byte)6)]
    public void MissingIndex_NamesTheTableAsWritten(string sql, string name, byte state)
        => AssertError($"create table dx (id int); {sql}", 3701, state, $"Cannot drop the index '{name}', because it does not exist or you do not have permission.");

    // ---- date functions ----

    [TestMethod]
    [DataRow("dateadd(hour, 1, cast('2020-01-01' as date))", "hour", "dateadd", "date", (byte)1)]
    [DataRow("dateadd(tzoffset, 1, cast('2020-01-01' as datetime))", "tzoffset", "dateadd", "datetime", (byte)0)]
    [DataRow("datepart(hour, cast('2020-01-01' as date))", "hour", "datepart", "date", (byte)2)]
    [DataRow("datepart(year, cast('10:00' as time))", "year", "datepart", "time", (byte)3)]
    [DataRow("datepart(tzoffset, cast('2020-01-01' as datetime))", "tzoffset", "datepart", "datetime", (byte)6)]
    [DataRow("datename(hour, cast('2020-01-01' as date))", "hour", "datename", "date", (byte)4)]
    [DataRow("datename(year, cast('10:00' as time))", "year", "datename", "time", (byte)5)]
    [DataRow("datename(tzoffset, cast('2020-01-01' as datetime))", "tzoffset", "datename", "datetime", (byte)7)]
    [DataRow("datetrunc(hour, cast('2020-01-01' as date))", "hour", "datetrunc", "date", (byte)10)]
    [DataRow("datetrunc(year, cast('10:00' as time))", "year", "datetrunc", "time", (byte)10)]
    [DataRow("date_bucket(hour, 1, cast('2020-01-01' as date))", "hour", "Date_Bucket", "date", (byte)1)]
    public void DatepartForAnotherType_RaisesMsg9810(string call, string datepart, string function, string type, byte state)
        => AssertError($"select {call}", 9810, state, $"The datepart {datepart} is not supported by date function {function} for data type {type}.");

    [TestMethod]
    public void DateTrunc_TruncatesATime()
        => AreEqual(new TimeSpan(10, 0, 0), new Simulation().ExecuteScalar("select datetrunc(hour, cast('10:42:17' as time))"));

    [TestMethod]
    [DataRow("switchoffset(sysdatetimeoffset(), 'x')", "switchoffset", (byte)0)]
    [DataRow("switchoffset(sysdatetimeoffset(), '+05')", "switchoffset", (byte)0)]
    [DataRow("switchoffset(sysdatetimeoffset(), '+5:00')", "switchoffset", (byte)0)]
    [DataRow("switchoffset(sysdatetimeoffset(), ' +05:00 ')", "switchoffset", (byte)0)]
    [DataRow("switchoffset(sysdatetimeoffset(), '+05:60')", "switchoffset", (byte)0)]
    [DataRow("switchoffset(sysdatetimeoffset(), '+14:01')", "switchoffset", (byte)0)]
    [DataRow("switchoffset(sysdatetimeoffset(), 900)", "switchoffset", (byte)1)]
    [DataRow("todatetimeoffset(sysdatetime(), 'x')", "todatetimeoffset", (byte)2)]
    [DataRow("todatetimeoffset(sysdatetime(), 900)", "todatetimeoffset", (byte)3)]
    public void InvalidOffset_RaisesMsg9812(string call, string function, byte state)
        => AssertError($"select {call}", 9812, state, $"The timezone provided to builtin function {function} is invalid.");

    [TestMethod]
    public void OffsetString_TakesTheFullRange()
        => AreEqual("+14:00|-14:00", new Simulation().ExecuteScalar("""
            select concat(datename(tzoffset, switchoffset(sysdatetimeoffset(), '+14:00')), '|',
                          datename(tzoffset, switchoffset(sysdatetimeoffset(), '-14:00')))
            """));

    // ---- states ----

    [TestMethod]
    [DataRow("select 1 where 'a' like 'a' escape 'ab'", "ab")]
    [DataRow("declare @e varchar(5) = ''; select 1 where 'a' like 'a' escape @e", "")]
    public void InvalidLikeEscape_IsState1(string sql, string escape)
        => AssertError(sql, 506, 1, $"The invalid escape character \"{escape}\" was specified in a LIKE predicate.");

    [TestMethod]
    [DataRow("default 1 default 2", "DEFAULT")]
    [DataRow("check (a > 0) check (a > 1)", "CHECK")]
    [DataRow("unique unique", "UNIQUE")]
    public void DoubledColumnConstraint_IsState0(string constraints, string kind)
        => AssertError($"create table t (a int {constraints})", 8148, 0, $"More than one column {kind} constraint specified for column 'a', table 't'.");

    [TestMethod]
    public void DoubledColumnPrimaryKey_IsFollowedByMsg8110()
    {
        var ex = new Simulation().AssertSqlError("create table t (a int primary key primary key)", 8148);
        AreEqual(8110, ex.Errors[1].Number);
    }

    [TestMethod]
    public void TranslateLengthMismatch_RaisesMsg9828()
        => new Simulation().AssertSqlError("select translate('abc', 'ab', 'x')", 9828);
}
