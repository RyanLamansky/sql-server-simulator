using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>TYPE_NAME</c>, <c>PARSENAME</c>, <c>ORIGINAL_DB_NAME</c>,
/// <c>GETANSINULL</c>, <c>STRING_ESCAPE</c>, <c>TRANSLATE</c> — the Tier-2
/// metadata-lookup scalars plus the string-additions batch.
/// </summary>
[TestClass]
public sealed class MetadataAndStringScalarTests
{
    [TestMethod]
    public void TypeName_56_ReturnsInt()
        => AreEqual("int", new Simulation().ExecuteScalar("select type_name(56)"));

    [TestMethod]
    public void TypeName_0_ReturnsVoidType()
        => AreEqual("void type", new Simulation().ExecuteScalar("select type_name(0)"));

    [TestMethod]
    public void TypeName_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select type_name(null)"));

    [TestMethod]
    public void TypeName_UserType_RoundTripsThroughTypeId()
        => AreEqual("MyType", new Simulation().ExecuteScalar("create type MyType as table (id int); select type_name(type_id('MyType'))"));

    [TestMethod]
    public void ParseName_Leaf_ReturnsLast()
        => AreEqual("d", new Simulation().ExecuteScalar("select parsename('a.b.c.d', 1)"));

    [TestMethod]
    public void ParseName_Schema_ReturnsSecondFromLast()
        => AreEqual("c", new Simulation().ExecuteScalar("select parsename('a.b.c.d', 2)"));

    [TestMethod]
    public void ParseName_FourthSegment_ReturnsFirst()
        => AreEqual("a", new Simulation().ExecuteScalar("select parsename('a.b.c.d', 4)"));

    [TestMethod]
    public void ParseName_OutOfRange_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select parsename('a.b.c.d', 5)"));

    [TestMethod]
    public void ParseName_NullName_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select parsename(null, 1)"));

    [TestMethod]
    public void ParseName_NullIndex_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select parsename('a.b', null)"));

    [TestMethod]
    public void OriginalDbName_NoInitialCatalog_ReturnsEmpty()
        => AreEqual("", new Simulation().ExecuteScalar("select original_db_name()"));

    [TestMethod]
    public void OriginalDbName_ReturnsInitialCatalog_NotCurrentDatabase()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.ConnectionString = "Initial Catalog=tempdb";
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "use master; select original_db_name()";
        AreEqual("tempdb", command.ExecuteScalar());
    }

    [TestMethod]
    public void GetAnsiNull_ReturnsOne()
        => AreEqual((short)1, new Simulation().ExecuteScalar("select getansinull()"));

    [TestMethod]
    public void GetAnsiNull_WithDbArg_ReturnsOne()
        => AreEqual((short)1, new Simulation().ExecuteScalar("select getansinull('simulated')"));

    [TestMethod]
    public void StringEscape_Quote_EscapedAsBackslashQuote()
        => AreEqual("a\\\"b", new Simulation().ExecuteScalar("select string_escape('a\"b', 'json')"));

    [TestMethod]
    public void StringEscape_Backslash_DoubledAsTwoBackslashes()
        => AreEqual("\\\\", new Simulation().ExecuteScalar("select string_escape('\\', 'json')"));

    [TestMethod]
    public void StringEscape_Newline_EscapedAsBackslashN()
        => AreEqual("a\\nb", new Simulation().ExecuteScalar("select string_escape('a' + char(10) + 'b', 'json')"));

    [TestMethod]
    public void StringEscape_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select string_escape(cast(null as varchar(10)), 'json')"));

    [TestMethod]
    public void Translate_SimpleSubstitution_Works()
        => AreEqual("axcy", new Simulation().ExecuteScalar("select translate('abcd', 'bd', 'xy')"));

    [TestMethod]
    public void Translate_NoMatch_ReturnsInputUnchanged()
        => AreEqual("hello", new Simulation().ExecuteScalar("select translate('hello', 'xyz', 'abc')"));

    [TestMethod]
    public void Translate_NullInput_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select translate(cast(null as varchar(10)), 'a', 'b')"));

    [TestMethod]
    public void Translate_UnequalLengths_RaisesMsg9828()
        => new Simulation().AssertSqlError("select translate('abcd', 'abc', 'xy')", 9828);

    /// <summary>
    /// PARSENAME reads the name as an identifier: delimited parts unquote,
    /// an empty part answers NULL, and a name it can't read — more than four
    /// parts, a trailing dot, a stray bracket, an unterminated quote —
    /// answers NULL for every part.
    /// </summary>
    [TestMethod]
    [DataRow("'a.b.c.d'", 1, "d")]
    [DataRow("'a.b.c.d.e'", 1, null)]
    [DataRow("'[a.b].c'", 2, "a.b")]
    [DataRow("'\"a.b\".c'", 2, "a.b")]
    [DataRow("'[a]]b].c'", 2, "a]b")]
    [DataRow("'a..c'", 2, null)]
    [DataRow("'a...d'", 4, "a")]
    [DataRow("'.a'", 1, "a")]
    [DataRow("'a.b.'", 2, null)]
    [DataRow("''", 1, null)]
    [DataRow("'[a'", 1, null)]
    [DataRow("'a]'", 1, null)]
    [DataRow("'[a]x'", 1, null)]
    [DataRow("' a . b '", 1, " b ")]
    public void ParseName_ReadsIdentifierSyntax(string name, int part, string? expected)
        => AreEqual(expected is null ? DBNull.Value : expected, new Simulation().ExecuteScalar($"select parsename({name}, {part})"));
}
