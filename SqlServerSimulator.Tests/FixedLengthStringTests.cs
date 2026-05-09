using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for fixed-length string and binary types (<c>char(N)</c>,
/// <c>nchar(N)</c>, <c>binary(N)</c>): right-padding, silent CAST truncation,
/// INSERT-time truncation errors (Msg 2628 / 8152), trailing-space-aware comparison,
/// and width-validation error variants.
/// </summary>
[TestClass]
public sealed class FixedLengthStringTests
{
    [TestMethod]
    public void Cast_String_PadsToDeclaredLength() => AreEqual("abc  ", ExecuteScalar("select cast('abc' as char(5))"));

    [TestMethod]
    public void Cast_NString_PadsNcharToDeclaredCodeUnits() => AreEqual("abc  ", ExecuteScalar("select cast(N'abc' as nchar(5))"));

    [TestMethod]
    public void Cast_Varbinary_PadsBinaryWithZeros()
    {
        var value = ExecuteScalar("select cast(0xCAFE as binary(5))") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0, 0 }, value);
    }

    [TestMethod]
    public void Cast_OversizeString_SilentlyTruncatesInCast()
    {
        // CAST silently truncates without raising — error is reserved for INSERT/UPDATE.
        AreEqual("hello", ExecuteScalar("select cast('hello world' as char(5))"));
    }

    [TestMethod]
    public void Cast_OversizeBinary_SilentlyTruncatesInCast()
    {
        var value = ExecuteScalar("select cast(0x0102030405060708 as binary(5))") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, value);
    }

    [TestMethod]
    public void Cast_EmptyString_PadsToFullWidth()
        => AreEqual("   ", ExecuteScalar("select cast('' as char(3))"));

    [TestMethod]
    public void Comparison_CharWithVarchar_StripsTrailingSpaces()
    {
        // ANSI trailing-space padding shared by every string-family type.
        AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = 'abc'"));
        AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = 'abc  '"));
    }

    [TestMethod]
    public void Comparison_DifferentDeclaredLengths_StillEqualByContent()
        => AreEqual(1, ExecuteScalar("select 1 where cast('abc' as char(5)) = cast('abc' as char(10))"));

    [TestMethod]
    public void CreateTable_InsertSelect_RoundTripsPaddedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c char(5))");
        _ = simulation.ExecuteNonQuery("insert into t values ('hi')");
        AreEqual("hi   ", simulation.ExecuteScalar("select c from t"));
    }

    [TestMethod]
    public void CreateTable_NCharRoundTrip_PadsToDeclaredCodeUnits()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c nchar(5))");
        _ = simulation.ExecuteNonQuery("insert into t values (N'hi')");
        AreEqual("hi   ", simulation.ExecuteScalar("select c from t"));
    }

    [TestMethod]
    public void CreateTable_BinaryRoundTrip_PadsWithZeros()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (b binary(5))");
        _ = simulation.ExecuteNonQuery("insert into t values (0xCAFE)");
        var value = simulation.ExecuteScalar("select b from t") as byte[];
        IsNotNull(value);
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0, 0 }, value);
    }

    [TestMethod]
    public void Insert_OversizeString_RaisesTruncationError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c char(5))");
        simulation.AssertSqlError("insert into t values ('toolong')", 2628,
            "String or binary data would be truncated in table 't', column 'c'. Truncated value: 'toolo'.");
    }

    [TestMethod]
    public void Insert_OversizeNChar_RaisesTruncationError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c nchar(5))");
        simulation.AssertSqlError("insert into t values (N'toolong')", 2628,
            "String or binary data would be truncated in table 't', column 'c'. Truncated value: 'toolo'.");
    }

    [TestMethod]
    public void Insert_OversizeBinary_RaisesTruncationError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (b binary(5))");
        var ex = simulation.AssertSqlError("insert into t values (0x010203040506)", 2628);
        Assert.StartsWith("String or binary data would be truncated in table 't', column 'b'.", ex.Message);
    }

    [TestMethod]
    public void Cast_CharZero_RaisesMsg1001()
        => AssertSqlMessage("select cast('a' as char(0))", "Line 1: Length or precision specification 0 is invalid.");

    [TestMethod]
    public void Cast_CharOversize_RaisesMsg131WithTypeWording()
        => AssertSqlMessage("select cast('a' as char(8001))",
            "The size (8001) given to the type 'char' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void Cast_NCharOversize_RaisesMsg131WithConvertSpecificationWording()
        => AssertSqlMessage("select cast(N'a' as nchar(4001))",
            "The size (4001) given to the convert specification 'nchar' exceeds the maximum allowed for any data type (4000).");

    [TestMethod]
    public void Cast_BinaryOversize_RaisesMsg131WithBinaryWording()
        => AssertSqlMessage("select cast(0xab as binary(8001))",
            "The size (8001) given to the type 'binary' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void CreateTable_CharOversize_RaisesMsg131WithColumnWording()
        => new Simulation().AssertSqlError("create table t (c char(8001))", 131,
            "The size (8001) given to the column 'c' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void CreateTable_NCharOversize_RaisesMsg2717ParameterWording()
        => new Simulation().AssertSqlError("create table t (c nchar(4001))", 2717,
            "The size (4001) given to the parameter 'c' exceeds the maximum allowed (4000).");

    [TestMethod]
    public void Cast_Default_NoParensInCastIs30()
    {
        // CAST without parens defaults to 30 (column declaration default is 1).
        AreEqual(30, ExecuteScalar("select datalength(cast('abc' as char))"));
    }

    [TestMethod]
    public void CreateTable_DefaultIsOne()
    {
        // Column declaration default is 1.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (c char)");
        _ = simulation.ExecuteNonQuery("insert into t values ('a')");
        AreEqual("a", simulation.ExecuteScalar("select c from t"));
        _ = simulation.AssertSqlError("insert into t values ('hello')", 2628);
    }

    [TestMethod]
    public void Cast_UidToCharBelow36_RaisesMsg8170()
        => AssertSqlMessage("select cast(NEWID() as char(35))",
            "Insufficient result space to convert uniqueidentifier value to char.");

    [TestMethod]
    public void Cast_UidToNCharBelow36_RaisesMsg8115WithNvarcharText()
    {
        // nchar's overflow message names "nvarchar" — same shared text path.
        AssertSqlMessage("select cast(NEWID() as nchar(35))",
        "Arithmetic overflow error converting expression to data type nvarchar.");
    }

    [TestMethod]
    public void Cast_VarcharToNvarchar_PreservesValue()
        => AreEqual("abc", ExecuteScalar("select cast('abc' as nvarchar(10))"));

    [TestMethod]
    public void Insert_WithLegacyCompatibility_RaisesMsg8152NoColumnDetail()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("alter database current set compatibility_level = 150");
        _ = simulation.ExecuteNonQuery("create table t (c char(5))");
        simulation.AssertSqlError("insert into t values ('toolong')", 8152, "String or binary data would be truncated.");
    }

    [TestMethod]
    public void Cast_CharToVarchar_TrailingSpacesPreservedInString()
    {
        // Padded form is part of the value; varchar comparison still strips via ANSI padding.
        AreEqual(1, ExecuteScalar("select 1 where cast(cast('abc' as char(5)) as varchar(10)) = 'abc'"));
    }
}
