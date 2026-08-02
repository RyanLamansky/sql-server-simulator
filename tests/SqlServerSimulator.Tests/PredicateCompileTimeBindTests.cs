using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Compile-time binding of predicates and their operands: a cross-collation
/// comparison (Msg 468 / 457), a legacy-LOB string-scalar argument (Msg 8116)
/// and an unknown column (Msg 207) all report while the statement compiles,
/// so an <strong>empty</strong> rowset raises exactly what a populated one
/// does. Every case is probe-confirmed against SQL Server 2025 (2026-08-01)
/// on empty tables.
/// </summary>
[TestClass]
public sealed class PredicateCompileTimeBindTests
{
    private const string Ci = "SQL_Latin1_General_CP1_CI_AS";
    private const string Cs = "SQL_Latin1_General_CP1_CS_AS";

    /// <summary>
    /// Two same-shaped tables in conflicting collations plus a legacy-LOB
    /// table. Every table stays <strong>empty</strong> — that is the whole
    /// point of the fixture.
    /// </summary>
    private static Simulation EmptyFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table c1 (x varchar(20) collate {Ci} null, nx nvarchar(20) collate {Ci} null, y int null);
            create table c2 (x varchar(20) collate {Cs} null, nx nvarchar(20) collate {Cs} null, y int null);
            create table lob (nt ntext null, t text null, im image null, v varchar(20) null)
            """);
        return sim;
    }

    // === Cross-collation comparison: Msg 468 on an empty rowset ===

    [TestMethod]
    [DataRow("select c1.y from c1, c2 where c1.x = c2.x", "equal to")]
    [DataRow("select c1.y from c1, c2 where c1.x <> c2.x", "not equal to")]
    [DataRow("select c1.y from c1 join c2 on c1.x = c2.x", "equal to")]
    [DataRow("select c1.y from c1, c2 where c1.x like c2.x", "like")]
    [DataRow("select c1.y from c1, c2 where c1.x in (c2.x, 'z')", "equal to")]
    [DataRow("select c1.y from c1, c2 where c1.x between c2.x and 'z'", "greater than or equal to")]
    [DataRow("select c1.y from c1, c2 where c1.x is distinct from c2.x", "is not")]
    [DataRow("select c1.y from c1 where c1.x in (select c2.x from c2)", "equal to")]
    [DataRow("select c1.y from c1 where c1.x > all (select c2.x from c2)", "greater than")]
    [DataRow("select count(*) from c1, c2 group by c1.y having max(c1.x) = max(c2.x)", "equal to")]
    [DataRow("select case when c1.x = c2.x then 1 else 0 end from c1, c2", "equal to")]
    [DataRow("select case c1.x when c2.x then 1 else 0 end from c1, c2", "equal to")]
    [DataRow("update c1 set y = 1 from c1, c2 where c1.x = c2.x", "equal to")]
    [DataRow("delete c1 from c1, c2 where c1.x = c2.x", "equal to")]
    [DataRow("merge c1 as tgt using c2 as src on tgt.x = src.x when matched then update set tgt.y = 1;", "equal to")]
    [DataRow("merge c1 as tgt using c2 as src on tgt.y = src.y when matched and tgt.x = src.x then update set tgt.y = 1;", "equal to")]
    public void EmptyRowset_CrossCollationComparison_Raises468(string sql, string operatorName)
    {
        var ex = EmptyFixture().AssertSqlError(sql, 468);
        AreEqual((byte)9, ex.State);
        AreEqual($"Cannot resolve the collation conflict between \"{Cs}\" and \"{Ci}\" in the {operatorName} operation.", ex.Message);
    }

    /// <summary>
    /// The single-table DML forms bind their WHERE against the target's own
    /// columns, so the conflict has to come through an explicit
    /// <c>COLLATE</c> — one operand at Explicit rank still can't resolve
    /// against another at Explicit rank.
    /// </summary>
    [TestMethod]
    [DataRow("update c1 set y = 1 where x collate " + Ci + " = x collate " + Cs)]
    [DataRow("delete c1 where x collate " + Ci + " = x collate " + Cs)]
    public void EmptyRowset_SingleTableDmlWhere_Raises468(string sql)
        => Contains("in the equal to operation", EmptyFixture().AssertSqlError(sql, 468).Message);

    /// <summary>
    /// Concatenation takes the implicit-conversion message instead — the
    /// result column has to name one collation. <c>ORDER BY</c> and
    /// <c>GROUP BY</c> over a concat expression report the same thing, and the
    /// ANSI <c>||</c> spelling names the <c>concat</c> operator where <c>+</c>
    /// names <c>add</c>.
    /// </summary>
    [TestMethod]
    [DataRow("select c1.x + c2.x from c1, c2", "add")]
    [DataRow("select c1.y from c1, c2 order by c1.x + c2.x", "add")]
    [DataRow("select count(*) from c1, c2 group by c1.x + c2.x", "add")]
    [DataRow("select c1.x || c2.x from c1, c2", "concat")]
    public void EmptyRowset_CrossCollationConcat_Raises457(string sql, string operatorName)
    {
        var ex = EmptyFixture().AssertSqlError(sql, 457);
        AreEqual((byte)1, ex.State);
        AreEqual(
            $"Implicit conversion of varchar value to varchar cannot be performed because the collation of the value is unresolved due to a collation conflict between \"{Cs}\" and \"{Ci}\" in {operatorName} operator.",
            ex.Message);
    }

    /// <summary>
    /// Unifying two value arms names the <c>CASE</c> operator, and
    /// <c>COALESCE</c> says <c>CASE</c> too — it desugars to one.
    /// </summary>
    [TestMethod]
    [DataRow("select case when c1.y = 1 then c1.x else c2.x end from c1, c2")]
    [DataRow("select coalesce(c1.x, c2.x) from c1, c2")]
    [DataRow("select iif(c1.y = 1, c1.x, c2.x) from c1, c2")]
    public void EmptyRowset_CrossCollationValueArms_Raises457NamingCase(string sql)
        => AreEqual(
            $"Implicit conversion of varchar value to varchar cannot be performed because the collation of the value is unresolved due to a collation conflict between \"{Cs}\" and \"{Ci}\" in CASE operator.",
            EmptyFixture().AssertSqlError(sql, 457).Message);

    /// <summary>
    /// <c>CONCAT</c> / <c>CONCAT_WS</c> resolve their operands' collations too,
    /// but name the conflict with <b>Msg 451</b> — a message of its own that
    /// carries the clause and the 1-based ordinal of the slot being settled.
    /// The separator argument participates like any other, and the reported
    /// pair is the operand that broke the fold followed by the collation
    /// accumulated to its left.
    /// </summary>
    [TestMethod]
    [DataRow("select concat(c1.x, c2.x) from c1, c2", "concat", "SELECT", 1)]
    [DataRow("select concat(c1.x, c2.x) from c1, c2 where 1 = 0", "concat", "SELECT", 1)]
    [DataRow("select c1.y, concat(c1.x, c2.x) from c1, c2", "concat", "SELECT", 2)]
    [DataRow("select concat(c1.x, c1.x, c2.x) from c1, c2", "concat", "SELECT", 1)]
    [DataRow("select concat_ws('-', c1.x, c2.x) from c1, c2", "concat_ws", "SELECT", 1)]
    [DataRow("select concat_ws(c1.x, c2.x, c2.x) from c1, c2", "concat_ws", "SELECT", 1)]
    [DataRow("select c1.y from c1, c2 order by concat(c1.x, c2.x)", "concat", "ORDER BY", 1)]
    [DataRow("select c1.y from c1, c2 order by c1.y, concat(c1.x, c2.x)", "concat", "ORDER BY", 2)]
    [DataRow("select count(*) from c1, c2 group by concat(c1.x, c2.x)", "concat", "GROUP BY", 2)]
    [DataRow("select count(*) from c1, c2 group by c1.y, concat(c1.x, c2.x)", "concat", "GROUP BY", 3)]
    [DataRow("select concat(c1.x, c2.x) into #t from c1, c2", "concat", "SELECT", 1)]
    public void EmptyRowset_CrossCollationConcat_Raises451(string sql, string operatorName, string clause, int ordinal)
    {
        var ex = EmptyFixture().AssertSqlError(sql, 451);
        AreEqual((byte)1, ex.State);
        AreEqual(
            $"Cannot resolve collation conflict between \"{Cs}\" and \"{Ci}\" in {operatorName} operator occurring in {clause} statement column {ordinal}.",
            ex.Message);
    }

    /// <summary>
    /// Two explicit <c>COLLATE</c> postfixes take the operator's own Msg 468
    /// instead — the same split every other operator makes between an
    /// unresolvable pair of overrides and one of column collations.
    /// </summary>
    [TestMethod]
    [DataRow("select concat(c1.x collate " + Cs + ", c1.x collate " + Ci + ") from c1", "concat")]
    [DataRow("select concat_ws('-', c1.x collate " + Cs + ", c1.x collate " + Ci + ") from c1", "concat_ws")]
    public void EmptyRowset_ConcatOfTwoExplicitCollations_Raises468(string sql, string operatorName)
    {
        var ex = EmptyFixture().AssertSqlError(sql, 468);
        AreEqual((byte)9, ex.State);
        AreEqual($"Cannot resolve the collation conflict between \"{Ci}\" and \"{Cs}\" in the {operatorName} operation.", ex.Message);
    }

    /// <summary>
    /// A target collation settles the conflict for the Unicode family without
    /// an error: real coerces the concat result into the column or variable
    /// being assigned rather than making the expression name a collation of its
    /// own.
    /// </summary>
    [TestMethod]
    [DataRow("insert c1 (nx) select concat(c1.nx, c2.nx) from c1, c2")]
    [DataRow("declare @v nvarchar(40); select @v = concat(c1.nx, c2.nx) from c1, c2")]
    [DataRow("update c1 set nx = concat(c1.nx, c2.nx) from c1, c2")]
    public void CrossCollationConcat_IntoAssignmentTarget_Succeeds(string sql)
        => AreEqual(0, EmptyFixture().ExecuteNonQuery(sql));

    /// <summary>
    /// A <c>varchar</c> whose collation never resolved has bytes in no known
    /// code page, so no target settles it — real refuses the implicit
    /// conversion with <b>Msg 456</b>, which names the operator that produced
    /// the conflict rather than the assignment consuming it.
    /// </summary>
    [TestMethod]
    [DataRow("insert c1 (x) select concat(c1.x, c2.x) from c1, c2")]
    [DataRow("declare @v varchar(40); select @v = concat(c1.x, c2.x) from c1, c2")]
    [DataRow("update c1 set x = concat(c1.x, c2.x) from c1, c2")]
    public void CrossCollationConcat_VarcharIntoAssignmentTarget_Raises456(string sql)
    {
        var ex = EmptyFixture().AssertSqlError(sql, 456);
        AreEqual((byte)1, ex.State);
        AreEqual(
            $"Implicit conversion of varchar value to varchar cannot be performed because the resulting collation is unresolved due to collation conflict between \"{Cs}\" and \"{Ci}\" in concat operator.",
            ex.Message);
    }

    /// <summary>
    /// <c>ISNULL</c> takes its first argument's collation outright rather than
    /// unifying, so it never conflicts (probe-confirmed: real returns the
    /// rowset). An explicit <c>COLLATE</c> on one arm outranks the other and
    /// resolves the same way.
    /// </summary>
    [TestMethod]
    [DataRow("select isnull(c1.x, c2.x) from c1, c2")]
    [DataRow("select case when c1.y = 1 then c1.x collate " + Ci + " else c2.x end from c1, c2")]
    [DataRow("select c1.y from c1, c2 where c1.x = c2.x collate " + Ci)]
    public void EmptyRowset_ResolvableCollationPair_Succeeds(string sql)
        => AreEqual(0, EmptyFixture().ExecuteScalar<int>($"select count(*) from ({sql}) q(a)"));

    // === Legacy-LOB string-scalar arguments: Msg 8116 on an empty rowset ===

    [TestMethod]
    [DataRow("select len(nt) from lob", "ntext", 1, "len")]
    [DataRow("select len(nt) from lob where 1 = 0", "ntext", 1, "len")]
    [DataRow("select v from lob where len(nt) > 1", "ntext", 1, "len")]
    [DataRow("select upper(im) from lob", "image", 1, "upper")]
    [DataRow("select lower(t) from lob", "text", 1, "lower")]
    [DataRow("select left(nt, 1) from lob", "ntext", 1, "left")]
    [DataRow("select right(nt, 1) from lob", "ntext", 1, "right")]
    [DataRow("select reverse(nt) from lob", "ntext", 1, "reverse")]
    [DataRow("select ascii(nt) from lob", "ntext", 1, "ascii")]
    [DataRow("select unicode(nt) from lob", "ntext", 1, "unicode")]
    [DataRow("select soundex(t) from lob", "text", 1, "soundex")]
    [DataRow("select string_escape(nt, 'json') from lob", "ntext", 1, "string_escape")]
    [DataRow("select ltrim(v, nt) from lob", "ntext", 2, "ltrim")]
    [DataRow("select rtrim(v, nt) from lob", "ntext", 2, "rtrim")]
    [DataRow("select replace(v, nt, v) from lob", "ntext", 2, "replace")]
    [DataRow("select replace(v, v, nt) from lob", "ntext", 3, "replace")]
    [DataRow("select translate(v, nt, v) from lob", "ntext", 2, "translate")]
    [DataRow("select stuff(nt, 1, 1, v) from lob", "ntext", 1, "stuff")]
    [DataRow("select stuff(v, 1, 1, nt) from lob", "ntext", 4, "stuff")]
    [DataRow("select replicate(nt, 2) from lob", "ntext", 1, "replicate")]
    [DataRow("select patindex(nt, v) from lob", "ntext", 1, "patindex")]
    [DataRow("select charindex(nt, v) from lob", "ntext", 1, "charindex")]
    [DataRow("select difference(nt, v) from lob", "ntext", 1, "difference")]
    [DataRow("select difference(v, im) from lob", "image", 2, "difference")]
    [DataRow("select string_agg(nt, ',') from lob", "ntext", 1, "string_agg")]
    [DataRow("select string_agg(v, nt) from lob", "ntext", 2, "string_agg")]
    public void EmptyRowset_LegacyLobArgument_Raises8116(string sql, string typeName, int argumentIndex, string functionName)
        => AreEqual(
            $"Argument data type {typeName} is invalid for argument {argumentIndex} of {functionName} function.",
            EmptyFixture().AssertSqlError(sql, 8116).Message);

    /// <summary>
    /// TRIM numbers its arguments in written order, so the source is argument
    /// 1 bare and argument 2 once a <c>chars FROM</c> prefix claims the first
    /// slot — and its function word is the only capitalized one real emits.
    /// </summary>
    [TestMethod]
    [DataRow("select trim(t) from lob", 1)]
    [DataRow("select trim('x' from t) from lob", 2)]
    [DataRow("select trim(t from t) from lob", 1)]
    public void EmptyRowset_TrimLegacyLob_NumbersArgumentsAsWritten(string sql, int argumentIndex)
        => AreEqual(
            $"Argument data type text is invalid for argument {argumentIndex} of Trim function.",
            EmptyFixture().AssertSqlError(sql, 8116).Message);

    /// <summary>
    /// DIFFERENCE is the one member that takes a <c>text</c> argument — it
    /// converts implicitly and evaluates where its own SOUNDEX refuses all
    /// three legacy types. An argument that is <em>read</em> rather than
    /// transformed takes a LOB too, which is how a legacy column is meant to
    /// be consumed.
    /// </summary>
    [TestMethod]
    [DataRow("select difference(t, v) from lob")]
    [DataRow("select charindex('a', nt) from lob")]
    [DataRow("select patindex('%a%', nt) from lob")]
    [DataRow("select datalength(nt) from lob")]
    [DataRow("select isnull(nt, nt) from lob")]
    public void EmptyRowset_LegacyLobInReadingSlot_Succeeds(string sql)
        => AreEqual(0, EmptyFixture().ExecuteScalar<int>($"select count(*) from ({sql}) q(a)"));

    // === Unknown columns in the per-row-resolved clauses: Msg 207 ===

    [TestMethod]
    [DataRow("select y from c1 where nosuch = 1")]
    [DataRow("select y from c1 group by y having nosuch = 1")]
    [DataRow("select count(*) from c1 group by nosuch")]
    [DataRow("select c1.y from c1 join c2 on c1.nosuch = c2.y")]
    [DataRow("update c1 set y = 1 where nosuch = 1")]
    [DataRow("update c1 set y = nosuch")]
    [DataRow("delete c1 where nosuch = 1")]
    [DataRow("merge c1 as tgt using c2 as src on tgt.nosuch = src.y when matched then update set tgt.y = 1;")]
    public void EmptyRowset_UnknownColumnInPredicate_Raises207(string sql)
        => AreEqual("Invalid column name 'nosuch'.", EmptyFixture().AssertSqlError(sql, 207).Message);

    // === Modules bind the same rules at CREATE ===

    [TestMethod]
    [DataRow("create view vcc as select c1.y from c1, c2 where c1.x = c2.x", 468)]
    [DataRow("create view vlob as select len(nt) as n from lob", 8116)]
    [DataRow("create procedure pcc as select c1.y from c1, c2 where c1.x = c2.x", 468)]
    [DataRow("create procedure plob as select len(nt) from lob", 8116)]
    [DataRow("create procedure pdead as begin if 1 = 0 select len(nt) from lob; end", 8116)]
    [DataRow("create function flob() returns int as begin return (select top 1 len(nt) from lob); end", 8116)]
    public void ModuleBody_CarryingABindError_FailsCreate(string sql, int expectedNumber)
    {
        var sim = EmptyFixture();
        _ = sim.AssertSqlError(sql, expectedNumber);
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from sys.objects where name in ('vcc', 'vlob', 'pcc', 'plob', 'pdead', 'flob')"));
    }

    // === The per-row paths are unchanged ===

    /// <summary>
    /// The runtime gates still fire on their own terms: a populated rowset
    /// reports the same number and wording, so the compile-time check mirrors
    /// the per-value one rather than replacing it.
    /// </summary>
    [TestMethod]
    [DataRow("select c1.y from c1, c2 where c1.x = c2.x", 468)]
    [DataRow("select len(nt) from lob", 8116)]
    public void PopulatedRowset_StillRaisesTheSameError(string sql, int expectedNumber)
    {
        var sim = EmptyFixture();
        _ = sim.ExecuteNonQuery("insert c1 (x, y) values ('a', 1); insert c2 (x, y) values ('a', 1); insert lob (nt) values (N'a')");
        _ = sim.AssertSqlError(sql, expectedNumber);
    }

    /// <summary>
    /// The check runs at parse, which a plan-cache hit skips — so the cached
    /// plan has to be invalidated when a column's collation changes. It is:
    /// every successful ALTER bumps the schema version the cache entry is
    /// stamped with, so the re-parse picks up the new conflict.
    /// </summary>
    [TestMethod]
    public void PlanCache_AlteringAColumnsCollation_InvalidatesTheCompatiblePlan()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table p1 (x varchar(20) collate {Ci} null);
            create table p2 (x varchar(20) collate {Ci} null)
            """);
        const string Query = "select count(*) from p1, p2 where p1.x = p2.x";
        AreEqual(0, sim.ExecuteScalar<int>(Query));
        AreEqual(0, sim.ExecuteScalar<int>(Query));
        _ = sim.ExecuteNonQuery($"alter table p2 alter column x varchar(20) collate {Cs} null");
        _ = sim.AssertSqlError(Query, 468);
    }

    /// <summary>
    /// <strong>Divergence.</strong> Real compiles a batch as a unit, so both
    /// families are uncatchable bind-time failures — probe-confirmed that a
    /// <c>TRY</c> / <c>CATCH</c> around either one never reaches the CATCH and
    /// the batch dies. The simulator's dispatch loop compiles each statement
    /// as it reaches it, so the error is an ordinary catchable one. Shared
    /// with every other compile-time error the simulator raises, not specific
    /// to these two.
    /// </summary>
    [TestMethod]
    [DataRow("select c1.y from c1, c2 where c1.x = c2.x", 468)]
    [DataRow("select len(nt) from lob", 8116)]
    public void BindError_IsCatchableHereButNotOnReal(string sql, int expectedNumber)
        => AreEqual(expectedNumber, EmptyFixture().ExecuteScalar<int>($"""
            begin try
                {sql};
            end try
            begin catch
                select error_number();
            end catch
            """));
}
