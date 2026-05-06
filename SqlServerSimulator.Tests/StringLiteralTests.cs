using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end coverage of the tokenized literal forms ('foo', N'foo',
/// 0xHEX) and the SQL Server semantics they exercise: ANSI trailing-space
/// padding for varchar/nvarchar equality, case-insensitive default collation,
/// accent-sensitive comparison, escape handling for embedded apostrophes, and
/// no-padding equality for varbinary.
/// </summary>
[TestClass]
public class StringLiteralTests
{
    [TestMethod]
    [DataRow("'a' = 'a'", 1)]
    [DataRow("'a' = 'A'", 1)]                  // case-insensitive default collation
    [DataRow("'a' = 'b'", 0)]
    [DataRow("'a' = 'a '", 1)]                 // ANSI trailing-space padding
    [DataRow("'a ' = 'a  '", 1)]
    [DataRow("'' = '   '", 1)]                 // empty equals all-spaces under padding
    [DataRow("'a' = ' a'", 0)]                 // leading spaces are significant
    [DataRow("'café' = 'cafe'", 0)]            // accent-sensitive
    [DataRow("'café' = 'CAFÉ'", 1)]            // accents preserved across case fold
    public void VarcharEquality(string condition, int expectedRows) =>
        AreEqual(expectedRows, new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    [TestMethod]
    [DataRow("N'a' = N'A'", 1)]
    [DataRow("N'Bjørn' = N'BJØRN'", 1)]
    [DataRow("N'Bjørn  ' = N'BJØRN'", 1)]
    [DataRow("N'a' = N'b'", 0)]
    public void NVarcharEquality(string condition, int expectedRows) =>
        AreEqual(expectedRows, new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    [TestMethod]
    [DataRow("'a' < 'b'", 1)]
    [DataRow("'a' < 'a'", 0)]
    [DataRow("'b' > 'a'", 1)]
    [DataRow("'a' <= 'A   '", 1)]                 // case fold + ANSI padding produce equality, so <= is true
    [DataRow("'a' >= 'A   '", 1)]
    [DataRow("'a' < 'A   '", 0)]                  // ... but strict less-than is false
    [DataRow("'apple' < 'banana'", 1)]
    [DataRow("'banana' > 'apple'", 1)]
    public void VarcharOrdering(string condition, int expectedRows) =>
        AreEqual(expectedRows, new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    [TestMethod]
    [DataRow("0x0102 < 0x0103", 1)]
    [DataRow("0x0103 > 0x0102", 1)]
    [DataRow("0x01 < 0x0100", 1)]                 // shorter is less when prefix matches (no padding)
    [DataRow("0x0100 > 0x01", 1)]
    [DataRow("0x01 = 0x01", 1)]
    public void VarbinaryOrdering(string condition, int expectedRows) =>
        AreEqual(expectedRows, new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    [TestMethod]
    [DataRow("'a' = null", 0)]                    // any comparison with NULL is UNKNOWN, no row
    [DataRow("null = 'a'", 0)]
    [DataRow("null = null", 0)]                   // even NULL = NULL is UNKNOWN
    [DataRow("'' = null", 0)]
    public void NullVsString_AlwaysUnknown(string condition, int expectedRows) =>
        AreEqual(expectedRows, new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    [TestMethod]
    [DataRow("0xDEAD = 0xDEAD", 1)]
    [DataRow("0xDEAD = 0xBEEF", 0)]
    [DataRow("0xdead = 0xDEAD", 1)]            // hex digits are case-insensitive in the literal
    [DataRow("0x01 = 0x0100", 0)]              // no padding for varbinary
    [DataRow("0x = 0x", 1)]                    // bodiless 0x is the empty varbinary
    [DataRow("0x = 0xAB", 0)]                  // empty != non-empty
    public void VarbinaryEquality(string condition, int expectedRows) =>
        AreEqual(expectedRows, new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    [TestMethod]
    public void HexLiteral_OddLengthZeroPadsHighNibble()
    {
        // 0xABC → 0x0ABC (high nibble defaulted to 0, matching SQL Server).
        AreEqual(1, new Simulation().ExecuteReader("select 1 where 0xABC = 0x0ABC").EnumerateRecords().Count());
    }

    [TestMethod]
    public void HexLiteral_BarePrefix_IsEmptyVarbinary()
    {
        // SQL Server accepts 0x with no hex body as a zero-length varbinary;
        // the simulator follows.
        var bytes = (byte[])new Simulation().ExecuteScalar("select 0x")!;
        IsEmpty(bytes);
    }

    [TestMethod]
    public void StringLiteral_EmbeddedApostropheEscape()
    {
        // 'foo''bar' parses as foo'bar.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(20) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 'foo''bar' )").ExecuteNonQuery();
        AreEqual("foo'bar", connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringLiteral_Unclosed_RaisesError()
    {
        var ex = Throws<System.Data.Common.DbException>(() => new Simulation().ExecuteReader("select 1 where 'unclosed = 1").EnumerateRecords().ToArray());
        Assert.Contains("Unclosed quotation mark", ex.Message);
    }

    [TestMethod]
    public void Insert_VarcharLiteral_StoresAndReadsBack()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(20) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 'hello' )").ExecuteNonQuery();
        AreEqual("hello", connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void Insert_NVarcharLiteral_StoresAndReadsBack()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v nvarchar(20) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( N'café' )").ExecuteNonQuery();
        AreEqual("café", connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void Insert_HexLiteral_StoresAndReadsBack()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varbinary(8) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 0xDEADBEEF )").ExecuteNonQuery();
        var read = (byte[]?)connection.CreateCommand("select v from t").ExecuteScalar();
        CollectionAssert.AreEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, read);
    }

    [TestMethod]
    public void FromTableWhere_FiltersByVarcharLiteral()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( name varchar(20) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 'Alice' ), ( 'Bob' ), ( 'Carol' )").ExecuteNonQuery();

        // Case-insensitive + ANSI padding: 'BOB ' matches 'Bob'.
        using var reader = connection.CreateCommand("select name from t where name = 'BOB '").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("Bob", reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByVarbinaryLiteral()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, payload varbinary(4) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1, 0xDEAD ), ( 2, 0xBEEF )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id from t where payload = 0xBEEF").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }
}
