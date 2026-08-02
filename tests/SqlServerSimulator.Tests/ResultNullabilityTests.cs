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
            mnn decimal(9, 2) not null, mnu decimal(9, 2) null);
        insert nb values (2020, null, 1, 'a', null, '2020-01-01', null, 1.5, null);
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
    // Arithmetic claims nullable however non-null the operands are.
    [DataRow("1 + 1", true)]
    [DataRow("nn + nn", true)]
    [DataRow("mnn + mnn", true)]
    [DataRow("nn * nn", true)]
    [DataRow("nn - nn", true)]
    [DataRow("nn / nn", true)]
    [DataRow("nn % nn", true)]
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
}
