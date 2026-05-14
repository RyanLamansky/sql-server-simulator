using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Closed-list <c>SET</c> session / connection / planner options —
/// parse-and-discard for grammar compatibility (no underlying state
/// modeling). Probe-confirmed verbatim against SQL Server 2025
/// (2026-05-14): unknown option name followed by ON/OFF/value raises
/// Msg 195; unknown name with nothing parseable after falls through
/// to the generic Msg 102. <c>SET @v = expr</c> / <c>SET IDENTITY_INSERT</c>
/// have semantic effect and are tested elsewhere.
/// </summary>
[TestClass]
public sealed class SetOptionTests
{
    private static int RunBatch(string commandText) => new Simulation().ExecuteNonQuery(commandText);

    [TestMethod]
    // Bool toggles — every entry in the OnOff family of the closed list.
    [DataRow("SET ANSI_NULLS ON")]
    [DataRow("SET ANSI_NULLS OFF")]
    [DataRow("SET QUOTED_IDENTIFIER ON")]
    [DataRow("SET ANSI_WARNINGS ON")]
    [DataRow("SET ANSI_PADDING ON")]
    [DataRow("SET CONCAT_NULL_YIELDS_NULL ON")]
    [DataRow("SET NUMERIC_ROUNDABORT OFF")]
    [DataRow("SET ARITHABORT ON")]
    [DataRow("SET ARITHIGNORE OFF")]
    [DataRow("SET XACT_ABORT ON")]
    [DataRow("SET FMTONLY OFF")]
    [DataRow("SET NOEXEC OFF")]
    [DataRow("SET FORCEPLAN OFF")]
    [DataRow("SET PARSEONLY OFF")]
    [DataRow("SET CURSOR_CLOSE_ON_COMMIT OFF")]
    [DataRow("SET ANSI_DEFAULTS ON")]
    [DataRow("SET REMOTE_PROC_TRANSACTIONS ON")]
    [DataRow("SET NO_BROWSETABLE OFF")]
    [DataRow("SET SHOWPLAN_TEXT OFF")]
    [DataRow("SET SHOWPLAN_ALL OFF")]
    [DataRow("SET SHOWPLAN_XML OFF")]
    [DataRow("SET DISABLE_DEF_CNST_CHK ON")]
    [DataRow("SET NOCOUNT ON")]
    [DataRow("SET IMPLICIT_TRANSACTIONS OFF")]
    // Multi-option comma form — OnOff-restricted. The five-toggle row is the
    // canonical EF Core SqlServer-provider session-bootstrap shape and was the
    // original motivating case for the closed-list parser.
    [DataRow("SET ANSI_NULLS, QUOTED_IDENTIFIER, CONCAT_NULL_YIELDS_NULL, ANSI_WARNINGS, ANSI_PADDING ON")]
    [DataRow("SET ANSI_NULLS, QUOTED_IDENTIFIER OFF")]
    // Integer-value options (ROWCOUNT / TEXTSIZE tokenize as ReservedKeyword and
    // dispatch through the separate switch arm; the others come through
    // UnquotedString → closed-list lookup).
    [DataRow("SET LOCK_TIMEOUT 5000")]
    [DataRow("SET TEXTSIZE 4096")]
    [DataRow("SET DATEFIRST 7")]
    [DataRow("SET ROWCOUNT 100")]
    [DataRow("SET QUERY_GOVERNOR_COST_LIMIT 1000")]
    // Identifier-value options. LANGUAGE accepts both bare identifier and
    // quoted-string literal forms.
    [DataRow("SET DATEFORMAT mdy")]
    [DataRow("SET LANGUAGE us_english")]
    [DataRow("SET LANGUAGE N'us_english'")]
    // IntegerOrIdent options.
    [DataRow("SET DEADLOCK_PRIORITY LOW")]
    [DataRow("SET DEADLOCK_PRIORITY 5")]
    // Binary value.
    [DataRow("SET CONTEXT_INFO 0x12345678")]
    // SET TRANSACTION ISOLATION LEVEL — all five levels.
    [DataRow("SET TRANSACTION ISOLATION LEVEL READ COMMITTED")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL SNAPSHOT")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE")]
    // SET STATISTICS sub-form.
    [DataRow("SET STATISTICS IO ON")]
    [DataRow("SET STATISTICS TIME OFF")]
    [DataRow("SET STATISTICS XML OFF")]
    [DataRow("SET STATISTICS PROFILE OFF")]
    public void Accepted_NoOp(string sql) => AreEqual(-1, RunBatch(sql));

    [TestMethod]
    [DataRow("SET BANANA ON", "BANANA")]
    [DataRow("SET ANSI_NULLS, BANANA, QUOTED_IDENTIFIER ON", "BANANA")]
    public void UnknownOption_RaisesMsg195(string sql, string unrecognizedName)
        => new Simulation().AssertSqlError(sql, 195, $"'{unrecognizedName}' is not a recognized SET option.");

    [TestMethod]
    public void UnknownOption_NoTrailingTokens_RaisesMsg102()
    {
        // Probe-confirmed: SQL Server returns the generic Msg 102 here
        // (no dedicated Msg 195 because there's nothing to disambiguate
        // SET option from arbitrary token sequence).
        var ex = new Simulation().AssertSqlError("SET BANANA", 102);
        Contains("BANANA", ex.Message);
    }

    [TestMethod]
    public void Composed_WithSubsequentStatement_AcceptsBoth()
    {
        // The session-bootstrap-then-real-query pattern: SET options first,
        // then a SELECT. Statement-boundary handling must let the SET parser
        // hand off cleanly.
        AreEqual(1, new Simulation().ExecuteScalar("SET ANSI_NULLS ON; SELECT 1"));
    }
}
