using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for string-literal column aliases in the SELECT list:
/// the <c>AS 'x'</c> form, the bare postfix <c>expr 'x'</c> form, the legacy
/// alias-on-left <c>'x' = expr</c> form, their <c>N'x'</c> variants, and the
/// empty-alias Msg 1038 rejection. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SelectListStringAliasTests
{
    private static (string Name, object Value) FirstColumn(string sql)
    {
        using var reader = new Simulation().ExecuteReader(sql);
        IsTrue(reader.Read());
        return (reader.GetName(0), reader.GetValue(0));
    }

    [TestMethod]
    public void As_SingleQuotedLiteral_NamesColumn()
    {
        var (name, value) = FirstColumn("select 1 as 'X'");
        AreEqual("X", name);
        AreEqual(1, value);
    }

    [TestMethod]
    public void BarePostfixLiteral_NamesColumn()
    {
        var (name, value) = FirstColumn("select 1 'X'");
        AreEqual("X", name);
        AreEqual(1, value);
    }

    [TestMethod]
    public void BarePostfixLiteral_AfterStringExpression_NamesColumn()
    {
        // T-SQL has no implicit string concatenation, so a string literal
        // directly after a complete string-literal expression is the alias.
        var (name, value) = FirstColumn("select 'val' 'X'");
        AreEqual("X", name);
        AreEqual("val", value);
    }

    [TestMethod]
    public void AliasOnLeft_StringLiteral_NamesColumn()
    {
        var (name, value) = FirstColumn("select 'X' = 1");
        AreEqual("X", name);
        AreEqual(1, value);
    }

    [TestMethod]
    public void As_NPrefixedLiteral_NamesColumn()
    {
        var (name, value) = FirstColumn("select 1 as N'X'");
        AreEqual("X", name);
        AreEqual(1, value);
    }

    [TestMethod]
    public void BarePostfix_NPrefixedLiteral_NamesColumn()
    {
        var (name, _) = FirstColumn("select 1 N'X'");
        AreEqual("X", name);
    }

    [TestMethod]
    public void AliasOnLeft_NPrefixedLiteral_NamesColumn()
    {
        var (name, value) = FirstColumn("select N'X' = 1");
        AreEqual("X", name);
        AreEqual(1, value);
    }

    [TestMethod]
    public void As_LiteralWithEscapedQuote_NamesColumnWithApostrophe()
    {
        var (name, _) = FirstColumn("select 1 as 'it''s'");
        AreEqual("it's", name);
    }

    /// <summary>
    /// Double-quoted delimited identifiers — under the default
    /// <c>QUOTED_IDENTIFIER ON</c>, <c>"X"</c> is an identifier alias
    /// exactly like <c>[X]</c>. The full QUOTED_IDENTIFIER surface
    /// (including the OFF string-literal reading) is covered in
    /// <c>QuotedIdentifierTests</c>.
    /// </summary>
    [TestMethod]
    public void As_DoubleQuotedIdentifier_IsIdentifierAlias()
    {
        var (name, _) = FirstColumn("select 1 as \"X Y\"");
        AreEqual("X Y", name);
    }

    [TestMethod]
    public void As_EmptyLiteral_RaisesMsg1038()
    {
        var ex = new Simulation().AssertSqlError("select 1 as ''", 1038);
        AreEqual(
            "An object or column name is missing or empty. For SELECT INTO statements, verify each column has a name. For other statements, look for empty alias names. Aliases defined as \"\" or [] are not allowed. Change the alias to a valid name.",
            ex.Message);
        AreEqual((byte)15, ex.Class);
        AreEqual((byte)4, ex.State);
    }

    [TestMethod]
    public void BarePostfix_EmptyLiteral_RaisesMsg1038()
        => _ = new Simulation().AssertSqlError("select 1 ''", 1038);

    [TestMethod]
    public void AliasOnLeft_EmptyLiteral_RaisesMsg1038()
        => _ = new Simulation().AssertSqlError("select '' = 1", 1038);

    [TestMethod]
    public void As_EmptyNPrefixedLiteral_RaisesMsg1038()
        => _ = new Simulation().AssertSqlError("select 1 as N''", 1038);

    [TestMethod]
    public void As_EmptyBrackets_RaisesMsg1038()
        => _ = new Simulation().AssertSqlError("select 1 as []", 1038);
}
