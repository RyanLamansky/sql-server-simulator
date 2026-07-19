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
    public void OriginalDbName_ReturnsSimulated()
        => AreEqual("simulated", new Simulation().ExecuteScalar("select original_db_name()"));

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
    public void Translate_UnequalLengths_RaisesMsg9819()
        => new Simulation().AssertSqlError("select translate('abcd', 'abc', 'xy')", 9819);
}
