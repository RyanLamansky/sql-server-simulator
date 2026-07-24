using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for double-quoted identifiers and the
/// <c>SET QUOTED_IDENTIFIER</c> / <c>SET ANSI_DEFAULTS</c> options that toggle
/// how <c>"…"</c> tokenizes: an identifier (delimited like <c>[…]</c>) under
/// the default ON, a varchar string literal (like <c>'…'</c>) under OFF. Also
/// covers the parse-time textual-order scoping (top-level persists to session,
/// dynamic SQL scoped to its batch, proc bodies ignore the SET), the
/// <c>@@OPTIONS</c> bit-256 reflection, and plan-cache separation by setting.
/// Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class QuotedIdentifierTests
{
    private static (string Name, object Value) FirstColumn(string sql)
    {
        using var reader = new Simulation().ExecuteReader(sql);
        IsTrue(reader.Read());
        return (reader.GetName(0), reader.GetValue(0));
    }

    private static (string Name, object Value) FirstColumn(DbConnection connection, string sql)
    {
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        IsTrue(reader.Read());
        return (reader.GetName(0), reader.GetValue(0));
    }

    private static object? Scalar(DbConnection connection, string sql)
        => connection.CreateCommand(sql).ExecuteScalar();

    // ----- Default QUOTED_IDENTIFIER ON: "…" is a delimited identifier -----

    [TestMethod]
    public void On_EscapedQuote_NamesColumnWithEmbeddedQuote()
    {
        var (name, _) = FirstColumn("select 1 as \"a\"\"b\"");
        AreEqual("a\"b", name);
    }

    [TestMethod]
    public void On_SpecialsInsideAreLiteral()
    {
        var (name, _) = FirstColumn("select 1 as \"a]b'c [d\"");
        AreEqual("a]b'c [d", name);
    }

    [TestMethod]
    public void On_ReservedWord_IsIdentifierAlias()
    {
        var (name, _) = FirstColumn("select 1 as \"select\"");
        AreEqual("select", name);
    }

    [TestMethod]
    public void On_SpacesOnly_IsIdentifierAlias()
    {
        var (name, _) = FirstColumn("select 1 as \"   \"");
        AreEqual("   ", name);
    }

    [TestMethod]
    public void On_UnknownQuotedColumn_RaisesMsg207()
        => new Simulation().AssertSqlError(
            "select \"nosuchcol\"", 207, "Invalid column name 'nosuchcol'.");

    [TestMethod]
    public void On_DdlRoundTrip_QuotedTableAndColumnNamesComeBackVerbatim()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table "QI T" ("C one" int, "C]two" int);
            insert "QI T" ("C one", "C]two") values (1, 2);
            select "C one" from "QI T"
            """);
        IsTrue(reader.Read());
        AreEqual("C one", reader.GetName(0));
        AreEqual(1, reader.GetValue(0));
    }

    [TestMethod]
    public void On_EmptyQuotedAlias_RaisesMsg1038()
    {
        var ex = new Simulation().AssertSqlError("select 1 as \"\"", 1038);
        AreEqual((byte)15, ex.Class);
        AreEqual((byte)4, ex.State);
    }

    [TestMethod]
    public void On_EmptyBracketColumn_RaisesMsg1038()
        => _ = new Simulation().AssertSqlError("create table t (c int); select [] from t", 1038);

    [TestMethod]
    public void On_EmptyQuotedTableName_RaisesMsg1038()
        => _ = new Simulation().AssertSqlError("create table \"\"(c int)", 1038);

    [TestMethod]
    public void On_UnclosedQuote_RaisesMsg105WithBody()
        => new Simulation().AssertSqlError(
            "select \"unclosed", 105,
            "Unclosed quotation mark after the character string 'unclosed'.");

    [TestMethod]
    public void On_NIsNotAPrefixForDoubleQuotes_RaisesMsg207ForN()
        => new Simulation().AssertSqlError(
            "select N\"foo\"", 207, "Invalid column name 'N'.");

    [TestMethod]
    public void On_AtAtOptionsBit256_IsSet()
        => AreEqual(256, new Simulation().ExecuteScalar("select @@OPTIONS & 256"));

    // ----- SET QUOTED_IDENTIFIER OFF: "…" is a varchar string literal -----

    [TestMethod]
    public void Off_DoubleQuoted_IsStringLiteral()
    {
        var (_, value) = FirstColumn("set quoted_identifier off; select \"abc\"");
        AreEqual("abc", value);
    }

    [TestMethod]
    public void Off_Concatenation_Works()
    {
        var (_, value) = FirstColumn("set quoted_identifier off; select \"a\" + 'b' + \"c\"");
        AreEqual("abc", value);
    }

    [TestMethod]
    public void Off_EscapedQuote_ProducesEmbeddedQuote()
    {
        var (_, value) = FirstColumn("set quoted_identifier off; select \"a\"\"b\"");
        AreEqual("a\"b", value);
    }

    [TestMethod]
    public void Off_ApostropheInside_IsLiteralCharacter()
    {
        var (_, value) = FirstColumn("set quoted_identifier off; select \"it's\"");
        AreEqual("it's", value);
    }

    [TestMethod]
    public void Off_EmptyDoubleQuoted_IsEmptyStringNotMsg1038()
    {
        var (_, value) = FirstColumn("set quoted_identifier off; select \"\"");
        AreEqual("", value);
    }

    [TestMethod]
    public void Off_QuotedAlias_IsStringLiteralAlias()
    {
        var (name, _) = FirstColumn("set quoted_identifier off; select 1 as \"X Y\"");
        AreEqual("X Y", name);
    }

    [TestMethod]
    public void Off_AtAtOptionsBit256_IsClear()
        => AreEqual(0, new Simulation().ExecuteScalar("set quoted_identifier off; select @@OPTIONS & 256"));

    [TestMethod]
    public void Off_BracketsAreStillIdentifiers()
    {
        var (name, _) = FirstColumn("set quoted_identifier off; select 1 as [ok]");
        AreEqual("ok", name);
    }

    [TestMethod]
    public void Off_UnclosedQuote_RaisesMsg105()
        => new Simulation().AssertSqlError(
            "set quoted_identifier off; select \"unclosed", 105,
            "Unclosed quotation mark after the character string 'unclosed'.");

    // ----- Parse-time / scoping semantics (session state across commands) -----

    [TestMethod]
    public void DeadBranchSet_AppliesTextuallyAndPersistsToSession()
    {
        using var connection = new Simulation().CreateOpenConnection();
        // The SET is in a never-taken IF branch, but QUOTED_IDENTIFIER applies
        // at parse in textual order regardless of control flow: "deadlit" reads
        // as a string literal in the same command...
        var (_, value) = FirstColumn(connection, "if 1 = 0 set quoted_identifier off; select \"deadlit\"");
        AreEqual("deadlit", value);
        // ...and the change persisted to the session, so a later separate
        // command also reads "…" as a literal.
        var (_, later) = FirstColumn(connection, "select \"x2\"");
        AreEqual("x2", later);
    }

    [TestMethod]
    public void ForwardOnly_StatementBeforeSetParsesUnderPriorSetting()
    {
        using var connection = new Simulation().CreateOpenConnection();
        // Session starts ON, so "a1" (before the SET) is an identifier → Msg 207,
        // even though a later statement in the same batch flips to OFF.
        var ex = Throws<SimulatedSqlException>(
            () => Scalar(connection, "select \"a1\"; set quoted_identifier off; select \"a2\""));
        AreEqual(207, ex.Number);
        AreEqual("Invalid column name 'a1'.", ex.Message);
    }

    [TestMethod]
    public void CrossCommand_SetPersistsToSubsequentCommand()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = Scalar(connection, "set quoted_identifier off");
        var (_, value) = FirstColumn(connection, "select \"still off\"");
        AreEqual("still off", value);
    }

    [TestMethod]
    public void DynamicSql_SetScopedToOwnBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var (_, value) = FirstColumn(connection, "exec('set quoted_identifier off; select \"in dyn\"')");
        AreEqual("in dyn", value);
        // The dynamic batch's flip did not leak to the session.
        AreEqual(256, Scalar(connection, "select @@OPTIONS & 256"));
    }

    [TestMethod]
    public void DynamicSql_SpExecuteSql_SetScopedToOwnBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var (_, value) = FirstColumn(connection, "exec sp_executesql N'set quoted_identifier off; select \"in dyn\"'");
        AreEqual("in dyn", value);
        AreEqual(256, Scalar(connection, "select @@OPTIONS & 256"));
    }

    [TestMethod]
    public void ProcedureBody_IgnoresSetAndLeavesSessionUntouched()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = Scalar(connection, "create procedure dbo.p as set quoted_identifier off; select 'made'");
        AreEqual("made", Scalar(connection, "exec dbo.p"));
        AreEqual(256, Scalar(connection, "select @@OPTIONS & 256"));
    }

    [TestMethod]
    public void AnsiDefaults_BundlesQuotedIdentifier()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = Scalar(connection, "set ansi_defaults off");
        var (_, asLiteral) = FirstColumn(connection, "select \"viaansidef\"");
        AreEqual("viaansidef", asLiteral);
        // ANSI_DEFAULTS ON restores identifier reading.
        _ = Scalar(connection, "set ansi_defaults on");
        var ex = Throws<SimulatedSqlException>(() => Scalar(connection, "select \"viaansidef\""));
        AreEqual(207, ex.Number);
    }

    [TestMethod]
    public void CommaMultiOption_AppliesQuotedIdentifier()
    {
        var (_, value) = FirstColumn("set quoted_identifier, ansi_nulls off; select \"comma\"");
        AreEqual("comma", value);
    }

    [TestMethod]
    public void PlanCache_SeparatesByQuotedIdentifierSetting()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = Scalar(connection, "set quoted_identifier off");
        // Same text executed twice under OFF — the second may replay from cache.
        AreEqual("abc", Scalar(connection, "select \"abc\""));
        AreEqual("abc", Scalar(connection, "select \"abc\""));
        // Flipping to ON must not replay the cached string-literal plan: the
        // same text now reads "abc" as an (invalid) identifier → Msg 207.
        _ = Scalar(connection, "set quoted_identifier on");
        var ex = Throws<SimulatedSqlException>(() => Scalar(connection, "select \"abc\""));
        AreEqual(207, ex.Number);
    }

    [TestMethod]
    // SQL Server allows @, $, # (and _) after the first char of a regular
    // unquoted identifier. ORMs emit crafted aliases like `crafted_alia$`
    // (Django's annotations tests) — probe-confirmed these tokenize + resolve.
    [DataRow("crafted_alia$")]
    [DataRow("col$1")]
    [DataRow("a#b")]
    [DataRow("x@y")]
    public void UnquotedIdentifier_AllowsAtDollarHashInBody(string alias)
        => AreEqual(7, new Simulation().ExecuteScalar<int>(
            $"select {alias}.n from (select 7 as n) as {alias}"));
}
