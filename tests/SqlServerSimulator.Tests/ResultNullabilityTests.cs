using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The projection-nullability table behind <c>Expression.ResultIsNullable</c> —
/// what the TDS COLMETADATA <c>fNullable</c> flag claims and what a
/// <c>SELECT … INTO</c> destination column is declared. Real derives both from
/// one inference (probe-confirmed: every row below reads the same through
/// <c>sys.dm_exec_describe_first_result_set</c> and through the
/// <c>SELECT … INTO</c> destination's <c>sys.columns.is_nullable</c>), so
/// asserting the cheaper SELECT INTO surface here pins the wire's flag too;
/// <c>ColumnNullabilityWireTests</c> in the SqlClient suite carries one wire
/// pin per cluster.
/// </summary>
/// <remarks>
/// Probed against SQL Server 2025 (2026-08-02) one call per built-in, each over
/// a non-null literal, a NOT NULL column and a nullable column. There is no
/// rule behind the per-function answers — <c>CEILING</c> propagates while
/// <c>ABS</c> is always nullable, <c>PI</c> is NOT NULL while <c>RAND</c> is
/// not — so the table is the specification.
/// </remarks>
[TestClass]
public sealed class ResultNullabilityTests
{
    /// <summary>
    /// Probe source: one column per (type, nullability) pair the table needs.
    /// The <c>nn</c> value doubles as a valid year and <c>knn</c> carries a
    /// small magnitude, so the built-ins with a narrow argument domain
    /// (<c>…FROMPARTS</c>, <c>EXP</c>) evaluate rather than raising — the row
    /// has to survive the projection for the destination table to exist.
    /// </summary>
    private const string Seed = """
        create table nb (
            nn int not null, nu int null, knn int not null,
            snn varchar(20) not null, snu varchar(20) null,
            dnn datetime not null, dnu datetime null,
            mnn decimal(9, 2) not null, mnu decimal(9, 2) null,
            bnn bigint not null, tnn tinyint not null,
            fnn float not null, rnn real not null, ynn smallmoney not null,
            p70 decimal(7, 0) not null, p90 decimal(9, 0) not null, p160 decimal(16, 0) not null,
            dtnn date not null, d2nn datetime2(7) not null, sdnn smalldatetime not null,
            vbnn varbinary(10) not null, bfx binary(4) not null, vmnn varbinary(max) not null);
        insert nb values (2020, null, 1, 'a', null, '2020-01-01', null, 1.5, null,
            1, 1, 1, 1, 1, 1, 1, 1, '2020-01-01', '2020-01-01', '2020-01-01',
            0x0102, 0x01020304, 0x0102);
        """;

    /// <summary>
    /// Projects <paramref name="projection"/> into a fresh table and reports
    /// the destination column's declared nullability.
    /// </summary>
    private static bool ProjectsNullable(string projection) =>
        (string?)new Simulation().ExecuteScalar($"""
            {Seed}
            select {projection} as probe into dest from nb;
            select IS_NULLABLE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'dest'
            """) == "YES";

    // --- Per-built-in table ---

    [TestMethod]
    // Propagating: NOT NULL exactly when every argument is.
    [DataRow("ceiling(mnn)", false)]
    [DataRow("ceiling(mnu)", true)]
    [DataRow("ceiling(5.5)", false)]
    [DataRow("ceiling(cast(mnn as decimal(9, 2)))", true)]  // a CAST argument is nullable
    [DataRow("floor(mnn)", false)]
    [DataRow("floor(mnu)", true)]
    [DataRow("round(mnn, 1)", false)]
    [DataRow("round(mnu, 1)", true)]
    [DataRow("round(mnn, nu)", true)]                       // the length argument counts
    [DataRow("round(mnn, 1, nu)", true)]                    // so does the function argument
    [DataRow("sign(nn)", false)]
    [DataRow("sign(nu)", true)]
    [DataRow("radians(nn)", false)]
    [DataRow("radians(nu)", true)]
    [DataRow("greatest(nn, 1)", false)]
    [DataRow("greatest(nn, nu)", true)]
    [DataRow("least(nn, 1)", false)]
    [DataRow("least(nu, 1)", true)]
    [DataRow("datefromparts(nn, 1, 1)", false)]
    [DataRow("datefromparts(nu, 1, 1)", true)]
    [DataRow("datetimefromparts(nn, 1, 1, 0, 0, 0, 0)", false)]
    [DataRow("datetime2fromparts(nn, 1, 1, 0, 0, 0, 0, 0)", false)]
    [DataRow("smalldatetimefromparts(nn, 1, 1, 0, 0)", false)]
    [DataRow("timefromparts(knn, 1, 1, 0, 0)", false)]
    [DataRow("datetimeoffsetfromparts(nn, 1, 1, 0, 0, 0, 0, 0, 0, 0)", false)]
    // Always NOT NULL, arguments or not.
    [DataRow("pi()", false)]
    [DataRow("getdate()", false)]
    [DataRow("getutcdate()", false)]
    [DataRow("sysdatetime()", false)]
    [DataRow("sysutcdatetime()", false)]
    [DataRow("sysdatetimeoffset()", false)]
    [DataRow("current_timestamp", false)]
    [DataRow("rowcount_big()", false)]
    [DataRow("min_active_rowversion()", false)]
    [DataRow("current_request_id()", false)]
    [DataRow("applock_test('public', 'r', 'Shared', 'session')", false)]
    [DataRow("concat(snu, snu)", false)]
    [DataRow("concat_ws(',', snu, snu)", false)]
    // Always nullable — including the near neighbours of the propagating group.
    [DataRow("abs(nn)", true)]
    [DataRow("abs(5)", true)]
    [DataRow("power(nn, 2)", true)]
    [DataRow("square(nn)", true)]
    [DataRow("sqrt(nn)", true)]
    [DataRow("exp(knn)", true)]
    [DataRow("log(nn)", true)]
    [DataRow("degrees(nn)", true)]
    [DataRow("cos(nn)", true)]
    [DataRow("checksum(nn)", true)]
    [DataRow("rand()", true)]
    [DataRow("newid()", true)]
    [DataRow("dateadd(day, 1, dnn)", true)]
    [DataRow("datepart(year, dnn)", true)]
    [DataRow("year(dnn)", true)]
    [DataRow("eomonth(dnn)", true)]
    [DataRow("len(snn)", true)]
    [DataRow("left(snn, 2)", true)]
    [DataRow("upper(snn)", true)]
    [DataRow("replace(snn, 'a', 'b')", true)]
    [DataRow("charindex('a', snn)", true)]
    [DataRow("cast(nn as int)", true)]
    [DataRow("convert(int, nn)", true)]
    [DataRow("try_cast(nn as int)", true)]
    [DataRow("choose(1, nn, 2)", true)]
    [DataRow("scope_identity()", true)]
    [DataRow("count(*)", true)]
    [DataRow("sum(nn)", true)]
    [DataRow("max(nn)", true)]
    public void BuiltIn_ProjectsProbedNullability(string projection, bool expected)
        => AreEqual(expected, ProjectsNullable(projection), projection);

    // --- @@-constants: every one but @@IDENTITY is NOT NULL ---

    [TestMethod]
    [DataRow("@@rowcount", false)]
    [DataRow("@@spid", false)]
    [DataRow("@@trancount", false)]
    [DataRow("@@error", false)]
    [DataRow("@@nestlevel", false)]
    [DataRow("@@dbts", false)]
    [DataRow("@@fetch_status", false)]
    [DataRow("@@cursor_rows", false)]
    [DataRow("@@lock_timeout", false)]
    [DataRow("@@textsize", false)]
    [DataRow("@@connections", false)]
    [DataRow("@@procid", false)]
    [DataRow("@@options", false)]
    [DataRow("@@identity", true)]
    public void AtAtConstant_ProjectsProbedNullability(string projection, bool expected)
        => AreEqual(expected, ProjectsNullable(projection), projection);

    // --- Operators ---

    [TestMethod]
    // Exact-numeric arithmetic claims nullable however non-null the operands are.
    [DataRow("1 + 1", true)]
    [DataRow("nn + nn", true)]
    [DataRow("mnn + mnn", true)]
    [DataRow("nn * nn", true)]
    [DataRow("nn - nn", true)]
    [DataRow("nn / nn", true)]
    [DataRow("nn % nn", true)]
    // An *approximate* result is the one exception: NOT NULL when both operands
    // are and each reaches the result type losing nothing. (`%` isn't here —
    // real refuses modulo over float outright.)
    [DataRow("fnn + fnn", false)]
    [DataRow("fnn * fnn", false)]
    [DataRow("fnn - fnn", false)]
    [DataRow("fnn / fnn", false)]
    [DataRow("rnn + rnn", false)]
    [DataRow("rnn * rnn", false)]
    [DataRow("fnn + nu", true)]                             // a nullable operand still decides
    [DataRow("fnn * cast(2 as float)", true)]               // a CAST operand is nullable in its own right
    // The conversion half: an operand keeps every value only where the target
    // mantissa holds its whole integral range — float's 15 digits, real's 7 —
    // and only when it carries no scale.
    [DataRow("fnn * nn", false)]                            // int (10 digits) fits float
    [DataRow("rnn * nn", true)]                             // …but not real
    [DataRow("fnn * bnn", true)]                            // bigint (19) fits neither
    [DataRow("fnn * tnn", false)]
    [DataRow("rnn * tnn", false)]                           // tinyint fits real
    [DataRow("fnn * 2", false)]
    [DataRow("rnn * 2", true)]
    [DataRow("fnn * 2.0", true)]                            // a scaled literal doesn't land on float's grid
    [DataRow("fnn * mnn", true)]                            // decimal(9, 2) — scale disqualifies it
    [DataRow("fnn * ynn", true)]                            // smallmoney carries scale 4
    [DataRow("fnn * p70", false)]                           // decimal(7, 0) fits both
    [DataRow("rnn * p70", false)]
    [DataRow("fnn * p90", false)]                           // decimal(9, 0) fits float
    [DataRow("fnn * p160", true)]                           // decimal(16, 0) exceeds float's 15
    // The rule folds along a chain the same way concatenation does.
    [DataRow("fnn * fnn * fnn", false)]
    [DataRow("fnn * mnn * fnn", true)]
    // String / binary `+` is concatenation, which propagates.
    [DataRow("'a' + 'b'", false)]
    [DataRow("snn + snn", false)]
    [DataRow("snn + snu", true)]
    [DataRow("snn + 'x'", false)]
    [DataRow("snn + snn + snn", false)]
    [DataRow("snn + snn + snu", true)]
    [DataRow("snn + cast(snn as varchar(9))", true)]
    [DataRow("0x01 + 0x02", false)]
    // The ANSI `||` operator is always concatenation.
    [DataRow("snn || snn", false)]
    [DataRow("snn || snu", true)]
    // Bitwise propagates.
    [DataRow("1 & 2", false)]
    [DataRow("nn & nn", false)]
    [DataRow("nn & nu", true)]
    [DataRow("nn | nn", false)]
    [DataRow("nn ^ nn", false)]
    [DataRow("~1", false)]
    [DataRow("~nn", false)]
    [DataRow("~nu", true)]
    // Unary minus is arithmetic, so only a folded constant is NOT NULL.
    [DataRow("-1", false)]
    [DataRow("-(1)", false)]
    [DataRow("-nn", true)]
    [DataRow("-mnn", true)]
    // Pass-through wrappers.
    [DataRow("(nn)", false)]
    [DataRow("(nu)", true)]
    [DataRow("(snn + snn)", false)]
    [DataRow("snn collate Latin1_General_CI_AS", false)]
    [DataRow("snu collate Latin1_General_CI_AS", true)]
    public void Operator_ProjectsProbedNullability(string projection, bool expected)
        => AreEqual(expected, ProjectsNullable(projection), projection);

    // --- The CASE family and the constant fold that precedes it ---

    [TestMethod]
    [DataRow("nullif(1, 2)", false)]                        // arms differ → the constant 1 survives
    [DataRow("nullif(1, 1)", true)]                         // arms match → folds to NULL
    [DataRow("nullif(5, null)", false)]                     // UNKNOWN takes the ELSE arm
    [DataRow("nullif(nn, 2)", true)]                        // unfoldable → the NULL arm survives
    [DataRow("coalesce(nn, 0)", false)]
    [DataRow("coalesce(nu, 0)", true)]
    [DataRow("coalesce(nn, nu)", true)]
    [DataRow("coalesce(nn, null)", true)]
    [DataRow("coalesce(null, 5)", false)]                   // the constant-NULL arm drops out
    [DataRow("coalesce(null, null, 5)", false)]
    [DataRow("coalesce(null, nn)", false)]
    [DataRow("coalesce(5, nu)", false)]                     // a constant non-NULL arm answers alone
    // A binary arm unifies by widening, which preserves every value, so the
    // NOT NULL operand keeps its answer at each of the family's widths.
    [DataRow("coalesce(vbnn, 0x00)", false)]
    [DataRow("coalesce(vbnn, 0x0000000000000000000000)", false)]
    [DataRow("coalesce(bfx, 0x00000000)", false)]
    [DataRow("coalesce(vmnn, 0x00)", false)]
    [DataRow("coalesce(vbnn, cast(0x00 as varbinary(10)))", true)]
    [DataRow("isnull(nu, 0)", false)]                       // ISNULL needs only one non-null operand
    [DataRow("isnull(nu, nu)", true)]
    [DataRow("iif(nn > 1, 1, 2)", false)]
    [DataRow("iif(1 = 1, 5, null)", false)]
    [DataRow("iif(1 = 2, 5, null)", true)]
    [DataRow("iif(1 = 1, nn, nu)", false)]
    [DataRow("iif(1 = 2, nn, nu)", true)]
    [DataRow("case when 1 = 1 then 5 end", false)]          // constant-true arm beats the implicit ELSE NULL
    [DataRow("case when 1 = 2 then 5 end", true)]
    [DataRow("case when 1 = 1 then nn end", false)]
    [DataRow("case when 1 = 1 then nu end", true)]
    [DataRow("case when 1 = 2 then nu end", true)]
    [DataRow("case when 1 = 2 then nu else 5 end", false)]
    [DataRow("case when 1 = 2 then 5 when 1 = 2 then 6 end", true)]
    [DataRow("case when null = 1 then 5 end", true)]        // UNKNOWN drops the arm like FALSE
    [DataRow("case when nn = 1 then 5 when 1 = 1 then nu end", true)]
    [DataRow("case when 1 = 1 then 5 when nn = 1 then nu end", false)]
    [DataRow("case when 1 = 1 then snn + snn end", false)]
    [DataRow("case 1 when 1 then 5 end", false)]            // the simple form folds too
    [DataRow("case 1 when 1 then nu end", true)]
    [DataRow("case 1 when 2 then nu end", true)]
    [DataRow("case nn when 1 then 5 end", true)]
    public void CaseFamily_ProjectsProbedNullability(string projection, bool expected)
        => AreEqual(expected, ProjectsNullable(projection), projection);

    /// <summary>
    /// The second half of the CASE-family rule: a surviving arm also answers
    /// for the conversion the arm unification put on it, and reads nullable
    /// when that conversion could alter the value. Loss, not failure, is the
    /// test — <c>decimal</c> → <c>float</c> and <c>datetime</c> →
    /// <c>datetime2</c> can't raise, and both read nullable.
    /// </summary>
    [TestMethod]
    // Exact numeric narrowed by the integer literal's own value width: the
    // unification takes decimal(9, 2) from the column because the literal is
    // one digit wide, then converts the literal from its declared int.
    [DataRow("coalesce(mnn, 0)", true)]
    [DataRow("coalesce(mnn, 100)", true)]
    [DataRow("coalesce(0, mnn)", true)]                     // the folded constant arm converts too
    [DataRow("coalesce(mnn, 2147483647)", false)]           // ten digits widen the result to decimal(12, 2)
    [DataRow("coalesce(mnn, 0.0)", false)]
    [DataRow("coalesce(mnn, tnn)", false)]                  // a tinyint column's three digits fit
    [DataRow("coalesce(mnn, nn)", false)]                   // an int column widens the result instead
    [DataRow("coalesce(ynn, nn)", true)]                    // smallmoney holds six integral digits
    [DataRow("coalesce(ynn, tnn)", false)]
    [DataRow("isnull(mnn, 0)", false)]                      // ISNULL takes argument one's type, converting nothing
    [DataRow("greatest(mnn, 1)", true)]
    [DataRow("least(mnn, 1)", true)]
    [DataRow("greatest(mnn, 2147483647)", false)]
    [DataRow("iif(nn = 1, mnn, 0)", true)]
    [DataRow("case when nn = 1 then mnn else 0 end", true)]
    // Approximate targets: exact only for an integral source whose whole range
    // lands on integers the mantissa represents (2^53 for float, 2^24 for real).
    [DataRow("coalesce(nn, fnn)", false)]
    [DataRow("coalesce(bnn, fnn)", true)]
    [DataRow("coalesce(mnn, fnn)", true)]                   // a scaled decimal has no exact binary form
    [DataRow("coalesce(p90, fnn)", false)]
    [DataRow("coalesce(p160, fnn)", true)]
    [DataRow("coalesce(p70, rnn)", false)]
    [DataRow("coalesce(p90, rnn)", true)]
    [DataRow("coalesce(nn, rnn)", true)]
    [DataRow("coalesce(rnn, fnn)", false)]                  // every real is a float
    [DataRow("coalesce(fnn, 1)", false)]
    [DataRow("coalesce(fnn, 1.5)", true)]
    // Date/time: range and grid.
    [DataRow("coalesce(dtnn, dnn)", true)]                  // date reaches back past datetime's 1753 floor
    [DataRow("coalesce(sdnn, dtnn)", true)]                 // and past smalldatetime's 1900 one
    [DataRow("coalesce(dtnn, d2nn)", false)]
    [DataRow("coalesce(dnn, d2nn)", true)]                  // datetime's 1/300-second grid isn't a tick grid
    [DataRow("coalesce(sdnn, dnn)", false)]                 // a whole minute is
    // Strings widen, so their unification converts nothing.
    [DataRow("coalesce(snn, 'abc')", false)]
    [DataRow("coalesce(snn, 'a literal longer than the column')", false)]
    public void ArmConversion_ProjectsProbedNullability(string projection, bool expected)
        => AreEqual(expected, ProjectsNullable(projection), projection);

    /// <summary>
    /// Which condition shapes real folds: every predicate that answers from its
    /// own operands, and not the subquery shapes.
    /// </summary>
    [TestMethod]
    [DataRow("case when 'a' like 'a' then 5 end", false)]
    [DataRow("case when 'a' like 'b' then 5 else null end", true)]
    [DataRow("case when 1 in (1, 2) then 5 end", false)]
    [DataRow("case when 1 between 0 and 2 then 5 end", false)]
    [DataRow("case when 1 = 1 and 2 = 2 then 5 end", false)]
    [DataRow("case when 1 = 1 or 1 = 2 then 5 end", false)]
    [DataRow("case when not (1 = 2) then 5 end", false)]
    [DataRow("case when 1 is not null then 5 end", false)]
    [DataRow("case when 1 is not distinct from 1 then 5 end", false)]
    [DataRow("case when exists (select 1) then 5 end", true)]  // a subquery never folds
    [DataRow("iif(1 in (1, 2), 5, null)", false)]
    public void FoldedCondition_ProjectsProbedNullability(string projection, bool expected)
        => AreEqual(expected, ProjectsNullable(projection), projection);

    // --- Structural rules, unchanged by the per-built-in table ---

    [TestMethod]
    [DataRow("nn", false)]
    [DataRow("nu", true)]
    [DataRow("snn", false)]
    [DataRow("snu", true)]
    [DataRow("1", false)]
    [DataRow("'x'", false)]
    [DataRow("1.5", false)]
    [DataRow("row_number() over (order by nn)", true)]
    [DataRow("(select top 1 nn from nb)", true)]
    public void StructuralRule_ProjectsProbedNullability(string projection, bool expected)
        => AreEqual(expected, ProjectsNullable(projection), projection);

    // --- Module output columns and the per-source NULL-fill map ---
    //
    // A view's and an inline TVF's sys.columns rows read the same inference,
    // so their metadata and a direct read of their body agree. A join reads
    // each source against a per-source NULL-fill map: LEFT and OUTER APPLY
    // NULL-fill the right operand, RIGHT the left spine, FULL both.
    // Probe-confirmed against SQL Server 2025 through
    // sys.dm_exec_describe_first_result_set.

    [TestMethod]
    public void ViewColumn_KeepsTheBaseColumnsNullability()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int not null, b int null)",
            "create view v as select a, b from t");
        AreEqual("a=0 b=1", sim.ExecuteScalar("""
            select string_agg(concat(c.name, '=', c.is_nullable), ' ') within group (order by c.column_id)
            from sys.columns c where c.object_id = object_id('v')
            """));
    }

    [TestMethod]
    public void ViewOverAnInnerJoin_KeepsBothSidesNullability()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int not null, v int null); create table u (id int not null, w int not null)",
            "create view v as select t.id, t.v, u.w from t join u on u.id = t.id");
        AreEqual("id=0 v=1 w=0", sim.ExecuteScalar("""
            select string_agg(concat(c.name, '=', c.is_nullable), ' ') within group (order by c.column_id)
            from sys.columns c where c.object_id = object_id('v')
            """));
    }

    [TestMethod]
    public void ViewOverALeftJoin_MakesTheNullFilledSideNullable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int not null, v int null); create table u (id int not null, w int not null)",
            "create view v as select t.id, u.w from t left join u on u.id = t.id");
        AreEqual("id=0 w=1", sim.ExecuteScalar("""
            select string_agg(concat(c.name, '=', c.is_nullable), ' ') within group (order by c.column_id)
            from sys.columns c where c.object_id = object_id('v')
            """));
    }

    [TestMethod]
    public void InlineTvfColumn_KeepsTheBaseColumnsNullability()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int not null, b int null)",
            "create function dbo.f() returns table return (select a, b from t)");
        AreEqual("a=0 b=1", sim.ExecuteScalar("""
            select string_agg(concat(c.name, '=', c.is_nullable), ' ') within group (order by c.column_id)
            from sys.columns c where c.object_id = object_id('dbo.f')
            """));
    }

    /// <summary>
    /// A destination column read from the NULL-filled side of an outer join
    /// has to land nullable, or the first NULL-extended row contradicts the
    /// table just created for it.
    /// </summary>
    [TestMethod]
    public void SelectInto_ThroughALeftJoin_DeclaresTheInnerSideNullable()
        => AreEqual("id=0 v=0 w=1", new Simulation().ExecuteScalar("""
            create table a (id int not null, v int not null);
            create table b (id int not null, w int not null);
            insert a values (1, 10), (2, 20);
            insert b values (1, 100);
            select a.id, a.v, b.w into dest from a left join b on b.id = a.id;
            select string_agg(concat(c.name, '=', c.is_nullable), ' ') within group (order by c.column_id)
            from sys.columns c where c.object_id = object_id('dest')
            """));
}
